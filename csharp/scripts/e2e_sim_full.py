#!/usr/bin/env python3
"""Real five OS-process SIM_FULL continuous 11-channel conveyor acceptance.

All sockets are 127.0.0.1. Test-only D4000+ are NOT physical PLC addresses.
Run separately from the single-piece SIM_LIGHT smoke (fixed ports).
"""
import argparse
import json
import os
import re
from pathlib import Path
import socket
import sqlite3
import subprocess
import sys
import tempfile
import time


def fins_read(address):
    request = bytes([128, 0, 2, 0, 1, 0, 0, 99, 0, 42, 1, 1, 130,
                     address >> 8, address & 255, 0, 0, 1])
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.settimeout(1)
        sock.sendto(request, ('127.0.0.1', 19600))
        response, peer = sock.recvfrom(256)
        assert peer[0] == '127.0.0.1' and len(response) == 16
        assert response[0] == 192 and response[9] == 42 and response[12:14] == b'\x00\x00'
        return response[14] * 256 + response[15]


def query(path, statement):
    if not path.is_file():
        return None
    try:
        with sqlite3.connect(path.resolve().as_uri() + '?mode=ro', uri=True, timeout=1) as db:
            return db.execute(statement).fetchall()
    except sqlite3.OperationalError as exc:
        if 'no such table' in str(exc) or 'unable to open database' in str(exc):
            return None
        raise


def scalar(path, statement):
    value = query(path, statement)
    return None if value is None or len(value) == 0 else value[0][0]


def verify(root, count, flip):
    master = root / 'master/production.db'
    slave = root / 'slave/production.db'
    ledger = root / 'master/sim-full-cycle.db'
    expect_exit = (flip - 1) if flip else count
    expected_counts = {
        (master, 'workpieces'): count, (slave, 'workpieces'): count,
        (master, 'channel_results'): 11 * count,
        (slave, 'channel_results'): 6 * count
    }
    for (path, table), expected in expected_counts.items():
        assert scalar(path, 'SELECT COUNT(*) FROM ' + table) == expected, (path, table)
        assert scalar(path, 'PRAGMA integrity_check') == 'ok', path

    identities = query(master, 'SELECT workpiece_id,identity_json,final_json FROM workpieces')
    peer_identities = query(slave, 'SELECT workpiece_id,identity_json FROM workpieces')
    assert len(identities) == len(peer_identities) == count
    assert {x[0]: x[1] for x in identities} == {x[0]: x[1] for x in peer_identities}
    assert len({x[0] for x in identities}) == count, 'WorkpieceId reuse'
    for _, identity, final in identities:
        wp = json.loads(identity)
        assert wp['workpieceId'] and wp['epoch'] == 1
        if final:
            decision = json.loads(final)
            assert decision['isSimulation'] is True and decision['disposition'] == 'good'
            assert len(decision['expected']) == len(decision['received']) == 10
            assert 'SIM_ONLY' in decision['proof']
    assert sum(x[2] is not None for x in identities) == expect_exit

    rows = query(ledger, 'SELECT token,identity_json,phase,disposition FROM sim_full_tokens ORDER BY token')
    assert len(rows) == count and [r[0] for r in rows] == list(range(1, count + 1)), rows
    id_set = {x[0] for x in identities}
    assert {json.loads(x[1])['workpieceId'] for x in rows} == id_set
    assert sum(x[2] == 'exited' for x in rows) == expect_exit
    for token, identity, phase, outcome in rows:
        decision = next(x[2] for x in identities if x[0] == json.loads(identity)['workpieceId'])
        assert (phase == 'exited') == (decision is not None), token
        assert outcome == ('Good' if phase == 'exited' else None), token
    if flip:
        assert rows[flip - 1][2] == 'registered', 'flipped exit token was released'
        assert all(x[2] != 'exited' for x in rows[flip - 1:]), 'later pieces overtook HOLD'

    for node, c, asset_count in [('master', 5, 6), ('slave', 6, 7)]:
        path = root / node / 'vision/vision-assets.sqlite'
        assert scalar(path, 'SELECT COUNT(*) FROM vision_results') == count * c, node
        assert scalar(path, 'SELECT COUNT(*) FROM vision_assets') == count * asset_count, node
        assert scalar(path, "SELECT COUNT(*) FROM vision_assets WHERE kind='parent'") == count, node
        assert scalar(path, "SELECT COUNT(*) FROM vision_assets WHERE kind='child'") == count * 2, node

    for sender, expected in [('master', count), ('slave', count * 6)]:
        path = root / sender / 'production.db'
        assert scalar(path, 'SELECT COUNT(*) FROM outbox WHERE acknowledged_utc IS NOT NULL') == expected, sender
        assert scalar(path, 'SELECT COUNT(*) FROM outbox') == expected, sender
    assert scalar(master, 'SELECT COUNT(*) FROM inbox') == count * 6
    assert scalar(slave, 'SELECT COUNT(*) FROM inbox') == count

    assert fins_read(4005) == expect_exit, 'virtual hardware released unexpected number'
    assert fins_read(4002) == (flip if flip else 0), 'FIFO exit token is wrong'
    assert fins_read(4006) == (count - expect_exit if flip else 0), 'sim conveyor active count wrong'
    if flip:
        assert scalar(master, 'SELECT COUNT(*) FROM workpieces WHERE final_json IS NULL') == count - expect_exit
    else:
        assert fins_read(4000) == 0
        assert scalar(ledger, "SELECT COUNT(*) FROM sim_full_tokens WHERE phase='registered'") == 0
    return True


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--evidence-root')
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--count', type=int, default=5)
    parser.add_argument('--flip-token', type=int, default=0)
    parser.add_argument('--repeat-barcode', action='store_true')
    parser.add_argument('--timeout', type=float, default=70.0)
    args = parser.parse_args()
    if args.count < 3 or args.count > 60 or args.flip_token not in (0, 3) or args.count < args.flip_token:
        parser.error('CI supports 3..60 virtual items and optional deterministic fault token=3')
    source = Path(__file__).resolve().parent.parent
    evidence = Path(args.evidence_root).resolve() if args.evidence_root else Path(tempfile.mkdtemp(prefix='aoi-simfull-'))
    if evidence.exists() and any(evidence.iterdir()):
        raise RuntimeError('SIM_FULL cannot reuse a nonempty evidence directory')
    evidence.mkdir(parents=True, exist_ok=True)
    host = source / 'src/AOI.Host/bin/Debug/net10.0/AOI.Host.dll'
    ui = source / 'src/AOI.Ui.Avalonia/bin/Debug/net10.0/AOI.Ui.Avalonia.dll'
    assert host.is_file() and ui.is_file(), 'Build AOI.slnx first'
    env = dict(os.environ)
    env.update({'AOI_SIM_MODE': 'SIM_FULL', 'AOI_SIM_CHANNEL_SET': 'ALL_11',
                'AOI_SIM_ITEM_COUNT': str(args.count), 'AOI_SIM_MAX_IN_FLIGHT': '3',
                'AOI_SIM_END_BARCODE_FLIP_TOKEN': str(args.flip_token),
                'AOI_SIM_REPEAT_BARCODE': '1' if args.repeat_barcode else '0',
                'DOTNET_CLI_TELEMETRY_OPTOUT': '1'})
    processes, logs = [], []
    def start(name, dll, *flags):
        stream = (evidence / (name + '.log')).open('w', encoding='utf-8')
        logs.append(stream)
        proc = subprocess.Popen([args.dotnet, str(dll), *flags], env=env, cwd=source,
                                stdout=stream, stderr=subprocess.STDOUT)
        processes.append((name, proc))
        print(f'SIM_FULL_PROCESS_STARTED name={name} pid={proc.pid}', flush=True)

    try:
        start('vplc', host, 'vplc', '--data', str(evidence))
        start('slave', host, 'slave', '--data', str(evidence))
        start('master', host, 'master', '--data', str(evidence))
        start('master-ui', ui, '--master', '--headless', '--once')
        start('slave-ui', ui, '--slave', '--headless', '--once')
        print('SIM_FULL_FIVE_PROCESSES_LAUNCHED', flush=True)
        target = args.flip_token - 1 if args.flip_token else args.count
        deadline = time.monotonic() + args.timeout
        while time.monotonic() < deadline:
            for name, proc in processes[:3]:
                if proc.poll() is not None:
                    raise RuntimeError(f'{name} terminated exit={proc.returncode}')
            n = scalar(evidence / 'master/production.db',
                       'SELECT COUNT(*) FROM workpieces')
            m = scalar(evidence / 'master/production.db',
                       'SELECT COUNT(*) FROM workpieces WHERE final_json IS NOT NULL')
            s = scalar(evidence / 'slave/production.db',
                       'SELECT COUNT(*) FROM channel_results')
            acknowledgements = scalar(evidence / 'slave/production.db',
                       'SELECT COUNT(*) FROM outbox WHERE acknowledged_utc IS NOT NULL')
            if n == args.count and m == target and s == args.count * 6 and acknowledgements == args.count * 6:
                if fins_read(4005) == target:
                    break
            time.sleep(0.15)
        else:
            raise TimeoutError('SIM_FULL timed out without complete concurrent production evidence')
        # A matched set must remain stable, especially when the front item is held.
        time.sleep(0.55)
        verify(evidence, args.count, args.flip_token)
        master_log = (evidence / 'master.log').read_text(encoding='utf-8')
        virtual_log = (evidence / 'vplc.log').read_text(encoding='utf-8')
        # Different OS scheduling can finish item #1 before slot #3 fills.
        # Demand evidence of genuine overlap, with a hard upper bound of three.
        observed = [int(v) for v in re.findall(
            r'SIM_FULL_VPLC_LOAD token=\d+ active=(\d+)', virtual_log)]
        assert observed and 2 <= max(observed) <= 3, (
            'Conveyor must show real overlap without exceeding three slots', observed)
        assert master_log.count('SIM_FULL_LOADED') == args.count
        assert master_log.count('SIM_FULL_EXITED') == target
        if args.flip_token:
            assert f'SIM_FULL_HOLD token={args.flip_token}' in master_log
        for name, proc in processes[3:]:
            assert proc.wait(timeout=10) == 0, name + ' UI failed'
            assert 'UI_HEADLESS_SNAPSHOT_OK' in (evidence / (name + '.log')).read_text(encoding='utf-8'), name
        with socket.create_connection(('127.0.0.1', 19621), timeout=3) as sock:
            with sock.makefile('r', encoding='utf-8') as view:
                snapshot = json.loads(view.readline())
        assert snapshot['simulationMode'] == 'SIM_FULL', snapshot['simulationMode']
        assert snapshot['realHardwareWritesEnabled'] is False
        overview = snapshot.get('production')
        assert overview and overview['total'] == args.count, overview
        assert overview['good'] == target and overview['reject'] == 0, overview
        assert overview['unfinished'] == args.count - target, overview
        assert len(overview['recent']) == args.count, overview
        assert len({row['workpieceId'] for row in overview['recent']}) == args.count
        assert all(row['capturedChannels'] == 11 for row in overview['recent']), overview
        assert sum(row['final'] == 'good' for row in overview['recent']) == target
        assert snapshot['latestWorkpiece'] is not None
        if args.repeat_barcode:
            barcodes = query(evidence / 'master/production.db', 'SELECT barcode FROM workpieces')
            assert len({x[0] for x in barcodes}) == 1, 'Duplicate barcode test was not exercised'
        result = 'HOLD' if args.flip_token else 'GOOD'
        print(f'SIM_FULL_E2E_PASS items={args.count} finals={target} outcome={result} '
              f'master_channels={args.count * 11} slave_channels={args.count * 6} '
              f'ack={args.count * 7} fifo=STRICT max_inflight={max(observed)} false_release=0 UI=2',
              flush=True)
        return 0
    except Exception as exc:
        print('SIM_FULL_E2E_FAIL ' + repr(exc), file=sys.stderr)
        for name, _ in processes:
            path = evidence / (name + '.log')
            if path.is_file():
                print(name + ':\n' + path.read_text(encoding='utf-8', errors='replace')[-2400:],
                      file=sys.stderr)
        return 1
    finally:
        for _, proc in reversed(processes):
            if proc.poll() is None:
                proc.terminate()
        for _, proc in processes:
            try: proc.wait(timeout=4)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait()
        for stream in logs:
            stream.close()
        print('SIM_FULL_EVIDENCE=' + str(evidence), flush=True)


if __name__ == '__main__':
    sys.exit(main())
