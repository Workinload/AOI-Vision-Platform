#!/usr/bin/env python3
"""Run REAL five Linux OS processes (3 hosts + 2 Avalonia headless UIs) using local FINS/TCP/IPC.

No network to PLC outside loopback. No production hardware support. Requires a prebuilt solution.
"""
import argparse
import json
import os
from pathlib import Path
import socket
import sqlite3
import subprocess
import sys
import tempfile
import time


def fins_read(word):
    sequence = 41
    cmd = bytes([0x80, 0, 2, 0, 1, 0, 0, 99, 0, sequence,
                 1, 1, 0x82, (word >> 8) & 255, word & 255, 0, 0, 1])
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as udp:
        udp.settimeout(2)
        udp.sendto(cmd, ('127.0.0.1', 19600))
        packet, sender = udp.recvfrom(4096)
        assert sender[0] == '127.0.0.1' and len(packet) == 16
        assert packet[:1] == b'\xc0' and packet[9] == sequence and packet[12:14] == b'\x00\x00'
        return (packet[14] << 8) | packet[15]


def sql_one(path, stmt):
    if not path.exists():
        return None
    # The DB file may appear before Core has committed its initial schema.
    # A missing table during startup means not ready yet, not a failed Core.
    try:
        with sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True, timeout=1) as db:
            return db.execute(stmt).fetchone()
    except sqlite3.OperationalError as e:
        if "no such table" in str(e) or "unable to open database" in str(e):
            return None
        raise


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--dotnet', default='dotnet')
    ap.add_argument('--evidence-root', default=None)
    ap.add_argument('--all-channels', action='store_true', help='Opt-in real five-process 9-device/11-channel SIM_LIGHT')
    args = ap.parse_args()
    root = Path(__file__).resolve().parent.parent
    runroot = Path(args.evidence_root).resolve() if args.evidence_root else Path(tempfile.mkdtemp(prefix='aoi-e2e-'))
    runroot.mkdir(parents=True, exist_ok=True)
    host = root / 'src/AOI.Host/bin/Debug/net10.0/AOI.Host.dll'
    ui = root / 'src/AOI.Ui.Avalonia/bin/Debug/net10.0/AOI.Ui.Avalonia.dll'
    assert host.is_file() and ui.is_file(), 'Build csharp/AOI.slnx first'
    procs = []
    logs = []
    def launch(label, dll, *flags):
        file = open(runroot / (label + '.log'), 'w', encoding='utf-8')
        logs.append(file)
        child_env = {**os.environ, 'DOTNET_CLI_TELEMETRY_OPTOUT': '1'}
        if args.all_channels:
            child_env['AOI_SIM_CHANNEL_SET'] = 'ALL_11'
        else:
            child_env.pop('AOI_SIM_CHANNEL_SET', None)
        p = subprocess.Popen([args.dotnet, str(dll), *flags], cwd=root, stdout=file, stderr=subprocess.STDOUT,
                             env=child_env)
        procs.append((label, p))
        print(f'STARTED {label} pid={p.pid}', flush=True)
        return p
    try:
        launch('virtual-plc', host, 'vplc', '--data', str(runroot))
        launch('slave-core', host, 'slave', '--data', str(runroot))
        launch('master-core', host, 'master', '--data', str(runroot))
        # Both Avalonia UIs are separate OS processes and connect to their own Core IPC endpoints.
        launch('master-ui', ui, '--master', '--headless', '--once')
        launch('slave-ui', ui, '--slave', '--headless', '--once')
        print('FIVE_OS_PROCESSES_LAUNCHED', flush=True)
        db_m = runroot / 'master/production.db'
        db_s = runroot / 'slave/production.db'
        deadline = time.monotonic() + 28
        decision = None
        while time.monotonic() < deadline:
            for label, p in procs[:3]:
                if p.poll() is not None:
                    raise RuntimeError(f'{label} stopped unexpectedly: exit={p.returncode}')
            row = sql_one(db_m, 'SELECT final_json FROM workpieces WHERE final_json IS NOT NULL LIMIT 1')
            if row:
                decision = json.loads(row[0]); break
            time.sleep(0.25)
        if decision is None:
            raise TimeoutError('Core failed to persist final result')
        assert decision['disposition'] == 'good' and decision['isSimulation'] is True, decision
        assert 'SIM_ONLY' in decision['proof'], decision
        master_count = sql_one(db_m, 'SELECT COUNT(*) FROM channel_results')[0]
        slave_count = sql_one(db_s, 'SELECT COUNT(*) FROM channel_results')[0]
        expected_master = 11 if args.all_channels else 2
        expected_slave = 6 if args.all_channels else 1
        assert master_count == expected_master and slave_count == expected_slave, (master_count, slave_count)
        expected_slave_acks = 6 if args.all_channels else 1
        # Master final persistence can precede Slave receipt of the last ACK.
        # The protocol guarantees eventual durable delivery, not same-tick ACK arrival.
        ack_deadline = time.monotonic() + 8
        while True:
            master_acks = sql_one(db_m, 'SELECT COUNT(*) FROM outbox WHERE acknowledged_utc IS NOT NULL')[0]
            slave_acks = sql_one(db_s, 'SELECT COUNT(*) FROM outbox WHERE acknowledged_utc IS NOT NULL')[0]
            if master_acks == 1 and slave_acks == expected_slave_acks:
                break
            if time.monotonic() >= ack_deadline:
                raise AssertionError(('durable_ack_timeout', master_acks, slave_acks, expected_slave_acks))
            time.sleep(0.1)
        # Output writes follow Final WAL commit; permit only bounded propagation time.
        plc_deadline = time.monotonic() + 4
        while (fins_read(1016), fins_read(2099)) != (1, 2):
            if time.monotonic() >= plc_deadline:
                raise AssertionError('virtual PLC never reached synthetic GOOD/DONE')
            time.sleep(0.1)
        for label, p in procs[3:]:
            if p.wait(timeout=20) != 0:
                raise RuntimeError(f'{label} failed: exit={p.returncode}')
            content = (runroot / (label + '.log')).read_text(encoding='utf-8')
            assert 'UI_HEADLESS_SNAPSHOT_OK' in content, (label, content[-1500:])
        if args.all_channels:
            # Both nodes retain independent image WALs: Master 5 channels + 1 shared
            # line parent; Slave 6 channels + 1 shared line parent.
            for node, channel_count, assets in [('master', 5, 6), ('slave', 6, 7)]:
                ledger = runroot / node / 'vision/vision-assets.sqlite'
                assert sql_one(ledger, 'SELECT COUNT(*) FROM vision_results')[0] == channel_count, node
                assert sql_one(ledger, 'SELECT COUNT(*) FROM vision_assets')[0] == assets, node
                assert sql_one(ledger, "SELECT COUNT(*) FROM vision_assets WHERE kind='parent'")[0] == 1, node
                assert sql_one(ledger, "SELECT COUNT(*) FROM vision_assets WHERE kind='child'")[0] == 2, node

            channels = [row[0] for row in sqlite3.connect(db_m).execute('SELECT channel_id FROM channel_results')]
            assert set(channels) == {f'CH{i:02d}' for i in range(1, 12)}, channels
            slave_channels = [row[0] for row in sqlite3.connect(db_s).execute('SELECT channel_id FROM channel_results')]
            assert set(slave_channels) == {'CH02','CH03','CH04','CH05','CH08','CH09'}, slave_channels
            # Unlike startup headless snapshots, fetch fresh Core->UI IPC after finalization.
            with socket.create_connection(('127.0.0.1', 19621), timeout=3) as ipc:
                ipc.settimeout(3)
                with ipc.makefile('r', encoding='utf-8') as reader:
                    snapshot = json.loads(reader.readline())
            assert snapshot['latestFinal']['disposition'] == 'good', snapshot.get('latestFinal')
            assert len(snapshot['channels']) == 11, len(snapshot['channels'])
            assert all(c['status'] == 'SIM_OK' for c in snapshot['channels']), snapshot['channels']
            # Independent QA reads the WAL/PNG bytes, not just the runtime's counters.
            audit = subprocess.run(
                [sys.executable, str(root / 'qa/integrity_audit.py'),
                 '--evidence-root', str(runroot), '--scenario', '11channels'],
                cwd=root, capture_output=True, text=True, timeout=15)
            assert audit.returncode == 0 and 'A5_INTEGRITY_PASS' in audit.stdout, (
                audit.stdout, audit.stderr)
            print('SIM_11CH_INDEPENDENT_AUDIT_PASS', flush=True)

            # Exercise the A7 component against the SAME live two-Core databases,
            # then validate a redacted, hash-chained, portable diagnostic bundle.
            cli = root / 'src/AOI.Diagnostics.Cli/bin/Debug/net10.0/AOI.Diagnostics.Cli.dll'
            assert cli.is_file(), 'Diagnostics CLI must be built by AOI.slnx'
            journal = runroot / 'diagnostics' / 'flight.jsonl'
            bundle = runroot / 'diagnostics' / 'evidence.zip'
            def diagnostic(*commands):
                proc = subprocess.run([args.dotnet, str(cli), *map(str, commands)],
                                      cwd=root, capture_output=True, text=True, timeout=15)
                assert proc.returncode == 0, (commands, proc.stdout, proc.stderr)
                return proc.stdout
            diagnostic('capture', runroot, journal)
            replay = json.loads(diagnostic('replay', journal))
            assert len(replay) == 1 and replay[0]['resultCount'] == 17, replay
            assert not any(f['severity'] in ('ERROR', 'CRITICAL')
                           for assessment in replay for f in assessment['findings']), replay
            diagnostic('bundle', journal, bundle)
            assert 'SIM_ONLY_BUNDLE_VERIFIED' in diagnostic('verify', bundle)
            with open(journal, encoding='utf-8') as flight:
                record = json.loads(flight.readline())
            assert all(w['identity']['barcode'] == '[REDACTED]'
                       for node in record['snapshot']['nodes']
                       for w in node['workpieces']), 'Flight recorder leaked barcode'
            print('SIM_11CH_DIAGNOSTICS_PASS nodes=2 results=17 redacted=1 chain=1 bundle=1',
                  flush=True)

            print('SIM_11CH_E2E_PASS master_results=11 slave_results=6 assets=13 persisted_acks=7 '
                  'decision=SIM_GOOD FINS_D1016=1 FINS_D2099=2 UI_IPC_CHANNELS=11', flush=True)
        else:
            print('SIM_E2E_PASS master_results=2 slave_results=1 persisted_acks=2 decision=SIM_GOOD FINS_D1016=1 FINS_D2099=2 UI=2',flush=True)
        print('EVIDENCE=' + str(runroot),flush=True)
        return 0
    except Exception as e:
        print('SIM_E2E_FAIL: ' + repr(e),flush=True)
        for label, p in procs:
            file = runroot / (label + '.log')
            if file.exists():
                print(f'==== {label} pid={p.pid} exit={p.poll()} ====')
                print(file.read_text(encoding='utf-8')[-3300:])
        return 1
    finally:
        for _, p in reversed(procs):
            if p.poll() is None: p.terminate()
        for _, p in procs:
            try: p.wait(timeout=3)
            except subprocess.TimeoutExpired: p.kill(); p.wait()
        for f in logs: f.close()


if __name__ == '__main__':
    raise SystemExit(main())
