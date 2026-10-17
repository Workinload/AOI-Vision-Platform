#!/usr/bin/env python3
"""A0 real five-process SIM_ONLY peer-break fail-closed regression.

Starts the REAL vPLC UDP server, both .NET Cores, and both Avalonia UI
headless clients. The Slave drops TCP only AFTER its first registration is
persisted/ACKed. Both Cores MUST enter durable SQLite interlocks, resume
metadata-only PeerLink, and never re-trigger cameras or final PLC output.
No mock PeerSession, fabricated result or real hardware write is permitted.
"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time

from e2e_sim_full import core_snapshot, fins_read, make_png, scalar, query


def check(root):
    master = root / 'master'
    slave = root / 'slave'
    for role, node in [('master', master), ('slave', slave)]:
        db = node / 'host-peer-interlock.sqlite'
        assert scalar(db, 'PRAGMA integrity_check') == 'ok', (role, 'sqlite integrity')
        latch = query(db,
            'SELECT reason_code, detail FROM host_peer_interlock WHERE singleton=1')
        assert latch and len(latch) == 1 and latch[0][0] == 'PEER_LINK_LOST', (role, latch)
        assert latch[0][1], (role, 'missing original peer fault reason')
        log = (root / (role + '.log')).read_text(encoding='utf-8')
        assert 'SIM_FULL_' + role.upper() + '_PEER_INTERLOCK_HOLD' in log, (
            role, 'peer fault was not surfaced to operator')
        assert 'READONLY_RECONNECTED' in log, (
            role, 'metadata-only connection was not restored')

    assert scalar(master / 'production.db',
                  'SELECT COUNT(*) FROM workpieces') >= 1, 'no real registered workpiece'
    assert scalar(slave / 'production.db',
                  'SELECT COUNT(*) FROM workpieces') >= 1, 'Slave did not commit registration'
    assert scalar(master / 'production.db',
                  'SELECT COUNT(*) FROM workpieces WHERE final_json IS NOT NULL') == 0, (
                  'Peer break must never produce an automatic final')
    assert fins_read(4005) == 0, 'Physical exit despite uncertain PeerLink'
    assert scalar(master / 'sim-full-cycle.db',
                  "SELECT COUNT(*) FROM sim_full_tokens WHERE phase='exited'") == 0
    assert not query(master / 'production-progress.db',
                     "SELECT event_json FROM a1_production_flow_events WHERE "
                     "json_extract(event_json,'$.kind')='ejectionConfirmed'")
    wire = root / 'p0-fins-wire.jsonl'
    assert wire.is_file(), 'Actual loopback vPLC raw UDP flight recorder is missing'
    packets = [json.loads(s) for s in wire.read_text(encoding='utf-8').splitlines()
               if s.strip()]
    forbidden = [p for p in packets
                 if p['IsWrite'] and p['Address'] in (1016, 2099)]
    assert not forbidden, ('A real FINS final-output datagram was sent after break',
                           forbidden[:2])
    assert 'SIM_ONLY_SLAVE_INJECT_PEER_DROP_AFTER_PERSISTED_REGISTRATION' in (
        root / 'slave.log').read_text(encoding='utf-8')
    return scalar(master / 'production.db', 'SELECT COUNT(*) FROM workpieces')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--evidence-root')
    parser.add_argument('--timeout', type=float, default=35)
    args = parser.parse_args()
    source = Path(__file__).resolve().parent.parent
    host = source / 'src/AOI.Host/bin/Debug/net10.0/AOI.Host.dll'
    ui = source / 'src/AOI.Ui.Avalonia/bin/Debug/net10.0/AOI.Ui.Avalonia.dll'
    assert host.is_file() and ui.is_file(), 'Build AOI.slnx Debug before running'
    root = (Path(args.evidence_root).resolve() if args.evidence_root else
            Path(tempfile.mkdtemp(prefix='aoi-a0-peerloss-')))
    if root.exists() and any(root.iterdir()):
        raise RuntimeError('Interlock evidence directory must be initially empty')
    root.mkdir(parents=True, exist_ok=True)

    image_root = root / 'operator-images'
    for channel in range(1, 8):
        make_png(image_root / f'CH{channel:02d}.png', bright_defect=channel == 6)
    make_png(image_root / 'LINE-S-8.png', line_parent=True)
    make_png(image_root / 'LINE-M-2.png', line_parent=True)

    env = dict(os.environ)
    env.update({
        'AOI_SIM_MODE': 'SIM_FULL',
        'AOI_SIM_CHANNEL_SET': 'ALL_11',
        'AOI_SIM_ITEM_COUNT': '5',
        'AOI_SIM_MAX_IN_FLIGHT': '3',
        'AOI_SIM_END_BARCODE_FLIP_TOKEN': '0',
        'AOI_SIM_PLC_PROFILE': 'LEGACY_PLC_SHADOW',
        'AOI_SIM_IMAGE_ROOT': str(image_root),
        'AOI_SIM_IMAGE_LAYOUT': 'FLAT',
        'AOI_SIM_DROP_PEER_AFTER_SLAVE_REGISTRATION': '1',
        'AOI_SIM_CONFIG_TOKEN': 'SIM_ONLY_E2E_ROI_OPERATOR_TOKEN_2026',
        'DOTNET_CLI_TELEMETRY_OPTOUT': '1'
    })
    processes, logfiles = [], []
    def start(name, dll, *options):
        file = (root / (name + '.log')).open('w', encoding='utf-8')
        logfiles.append(file)
        proc = subprocess.Popen([args.dotnet, str(dll), *options],
                                cwd=source, env=env, stdout=file,
                                stderr=subprocess.STDOUT)
        processes.append((name, proc))
        print('A0_PEER_LOSS_STARTED name=' + name + ' pid=' + str(proc.pid),
              flush=True)

    try:
        start('vplc', host, 'vplc', '--data', str(root))
        start('slave', host, 'slave', '--data', str(root))
        start('master', host, 'master', '--data', str(root))
        start('master-ui', ui, '--master', '--headless', '--once')
        start('slave-ui', ui, '--slave', '--headless', '--once')
        deadline = time.monotonic() + args.timeout
        while time.monotonic() < deadline:
            for name, proc in processes[:3]:
                if proc.poll() is not None:
                    raise RuntimeError(name + ' terminated during TCP reconciliation; rc=' +
                                       str(proc.returncode))
            m = (root / 'master.log').read_text(encoding='utf-8')
            s = (root / 'slave.log').read_text(encoding='utf-8')
            if ('SIM_FULL_MASTER_PEER_READONLY_RECONNECTED' in m and
                'SIM_FULL_SLAVE_PEER_READONLY_RECONNECTED' in s and
                scalar(root / 'master/host-peer-interlock.sqlite',
                       'SELECT COUNT(*) FROM host_peer_interlock') == 1 and
                scalar(root / 'slave/host-peer-interlock.sqlite',
                       'SELECT COUNT(*) FROM host_peer_interlock') == 1):
                break
            time.sleep(0.1)
        else:
            raise TimeoutError('Actual Slave disconnect did not durably interlock both Cores')
        baseline = check(root)
        # Prove the network-only reconnection does NOT restart production after
        # the first durable safety latch. This is an observed stable interval,
        # not a guess from one frame snapshot.
        time.sleep(1.2)
        assert check(root) == baseline, 'Master accepted another physical item after peer loss'
        for name, proc in processes[:3]:
            assert proc.poll() is None, name + ' did not remain alive for operator'
        for name, proc in processes[3:]:
            assert proc.wait(timeout=10) == 0, name + ' real UI failed'
            log = (root / (name + '.log')).read_text(encoding='utf-8')
            assert 'UI_HEADLESS_SNAPSHOT_OK' in log, name + ' IPC died'

        for port in (19621, 19622):
            snap = core_snapshot(port)
            assert snap['realHardwareWritesEnabled'] is False, (
                port, 'sim-only field write guard')
        # A new Core process must NOT silently erase the existing WAL and
        # restart uncertain PLC outputs. The original Core stays online.
        restarted = subprocess.run([args.dotnet, str(host), 'master',
                                    '--data', str(root)],
                                   cwd=source, env=env, capture_output=True,
                                   text=True, timeout=10)
        assert restarted.returncode != 0 and 'SIM_FULL_RESTART_HOLD' in (
            restarted.stdout + restarted.stderr), (
            'unsafe automatic Core process restart was allowed')
        print('A0_PEER_LOSS_INTERLOCK_E2E_PASS five_processes=1 ' +
              'slave_injected_tcp_drop=1 wal_interlocks=2 ' +
              'metadata_reconnected=2 master_core_alive=1 slave_core_alive=1 ' +
              'no_camera_retrigger=1 no_final_plc_output=1 ' +
              'no_physical_exit=1 operator_reconciliation_required=1 ' +
              'process_restart_blocked=1 real_hardware_writes=0', flush=True)
        return 0
    except Exception as exc:
        print('A0_PEER_LOSS_INTERLOCK_E2E_FAIL ' + repr(exc), file=sys.stderr)
        for name, _ in processes:
            file = root / (name + '.log')
            if file.is_file():
                print(name + ':\n' +
                      file.read_text(encoding='utf-8', errors='replace')[-2800:],
                      file=sys.stderr)
        return 1
    finally:
        for _, proc in reversed(processes):
            if proc.poll() is None:
                proc.terminate()
        for _, proc in processes:
            try:
                proc.wait(timeout=4)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait()
        for logfile in logfiles:
            logfile.close()
        print('A0_PEER_LOSS_EVIDENCE=' + str(root), flush=True)


if __name__ == '__main__':
    sys.exit(main())
