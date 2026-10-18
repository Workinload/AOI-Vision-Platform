#!/usr/bin/env python3
"""Agent05 independent five-real-OS-process extension for final-output P0.

Runs the SAME A0 Host binaries and A2 localhost PLC, but unlike the A0
normal-completion runner, waits for durable A1/A7 terminal evidence before
stopping an intentionally held workpiece. This never invents an ACK or sends
FINS writes. A read-only FINS probe proves the PLC's actual applied state.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import time

from p0_claim_fins_antitamper import verify_case
from p0_fins_timeline_audit import AuditFailure

# Reuse the existing A0 SIM_ONLY PNG and independent FINS READ helpers.
sys.path.insert(0, str(Path(__file__).resolve().parent.parent / 'scripts'))
from e2e_sim_full import fins_read, make_png  # noqa: E402


def real_five_process_case(source: Path, root: Path, scenario: str, dotnet: str,
                           deadline_seconds: float) -> dict:
    if root.exists() and any(root.iterdir()):
        raise RuntimeError('Refuse to replace real five-process run: ' + str(root))
    root.mkdir(parents=True, exist_ok=True)
    host = source / 'src/AOI.Host/bin/Debug/net10.0/AOI.Host.dll'
    ui = source / 'src/AOI.Ui.Avalonia/bin/Debug/net10.0/AOI.Ui.Avalonia.dll'
    if not host.is_file() or not ui.is_file():
        raise RuntimeError('Actual .NET 10 Host/UI Debug binaries missing; build AOI.slnx first')
    image_root = root / 'operator-images'
    for channel in range(1, 8):
        make_png(image_root / f'CH{channel:02d}.png', bright_defect=(channel == 6))
    make_png(image_root / 'LINE-S-8.png', line_parent=True)
    make_png(image_root / 'LINE-M-2.png', line_parent=True)
    env = dict(os.environ)
    env.update({
        'AOI_SIM_MODE': 'SIM_FULL', 'AOI_SIM_CHANNEL_SET': 'ALL_11',
        'AOI_SIM_ITEM_COUNT': '3', 'AOI_SIM_MAX_IN_FLIGHT': '3',
        'AOI_SIM_PLC_PROFILE': 'LEGACY_PLC_SHADOW',
        'AOI_SIM_END_BARCODE_FLIP_TOKEN': '3' if scenario == 'ack-second' else '0',
        'AOI_SIM_END_SCAN_FAIL_TOKENS': '3' if scenario == 'scan-failed' else '',
        'AOI_SIM_END_LOSE_ACK': '3:1016' if scenario == 'ack-second' else '',
        'AOI_SIM_END_REJECT_WRITE': '',
        'AOI_SIM_ST05_ORDER': 'REQUEST_FIRST',
        'AOI_SIM_IMAGE_ROOT': str(image_root), 'AOI_SIM_IMAGE_LAYOUT': 'FLAT',
        'AOI_SIM_CONFIG_TOKEN': 'SIM_ONLY_E2E_ROI_OPERATOR_TOKEN_2026',
        'AOI_SIM_DROP_PEER_AFTER_SLAVE_REGISTRATION': '0',
        'DOTNET_CLI_TELEMETRY_OPTOUT': '1',
    })
    handles = []
    processes = []

    def launch(name: str, dll: Path, *arguments):
        logfile = (root / (name + '.log')).open('w', encoding='utf8')
        handles.append(logfile)
        process = subprocess.Popen([dotnet, str(dll), *arguments], cwd=source,
                                   env=env, stdout=logfile, stderr=subprocess.STDOUT)
        processes.append((name, process))
        print(f'A5_FIVE_PROCESS_LIVE name={name} pid={process.pid}', flush=True)

    def logtext(name: str) -> str:
        path = root / (name + '.log')
        return path.read_text(encoding='utf8', errors='replace') if path.exists() else ''

    try:
        launch('vplc', host, 'vplc', '--data', str(root))
        launch('slave', host, 'slave', '--data', str(root))
        launch('master', host, 'master', '--data', str(root))
        launch('master-ui', ui, '--master', '--headless', '--once')
        launch('slave-ui', ui, '--slave', '--headless', '--once')
        limit = time.monotonic() + deadline_seconds
        last = 'not yet received real A1/A2/A7 terminal evidence'
        passed = None
        while time.monotonic() < limit:
            for name, process in processes[:3]:
                if process.poll() is not None:
                    raise RuntimeError(f'{name} Core exited prematurely: {process.returncode}')
            try:
                passed = verify_case(root, scenario)
                break
            except (AuditFailure, sqlite3.Error, OSError, ValueError, KeyError) as exc:
                last = str(exc)
            time.sleep(0.15)
        if passed is None:
            raise TimeoutError(f'{scenario} A1/A2/A7 real WAL never converged: {last}')

        for name, process in processes[3:]:
            process.wait(timeout=12)
            if process.returncode != 0 or 'UI_HEADLESS_SNAPSHOT_OK' not in logtext(name):
                raise RuntimeError(f'{name} actual Core IPC snapshot failed: {logtext(name)[-1200:]}')
        # Do not accept a transient valid audit before an unintended retransmission.
        time.sleep(0.85)
        after = verify_case(root, scenario)
        if any(after[key] != passed[key] for key in ('claims', 'writes', 'readbacks', 'scannerReceipts')):
            raise RuntimeError(f'{scenario} actual A1/A7 claim or FINS final effect changed after stabilization')
        expected_2099 = 4 if scenario == 'ack-second' else 3
        # Unknown step2 D1016 after apply still means the actual device reads 2.
        if fins_read(2099) != expected_2099 or fins_read(1016) != 2:
            raise RuntimeError(f'{scenario} actual independent FINS READ contradicts physical output')
        verify_case(root, scenario)
        if 'AOI_HOST_FATAL' in logtext('master') + logtext('slave') + logtext('vplc'):
            raise RuntimeError('Actual AOI Core unexpectedly fatal during fault')
        if ('SIM_LEGACY_ABNORMAL_OUTPUT_UNCERTAIN token=3' not in logtext('master')
                and scenario == 'ack-second'):
            raise RuntimeError('Master did not log actual second-write unknown ACK')
        # Optional restart probe: only UNKNOWN ACK must block another physical send.
        if scenario == 'ack-second':
            master_process = processes[2][1]
            master_process.terminate()
            master_process.wait(timeout=7)
            restarted = subprocess.run([dotnet, str(host), 'master', '--data', str(root)],
                                       cwd=source, env=env, capture_output=True, text=True,
                                       timeout=15)
            (root / 'a5-master-restart-probe.log').write_text(
                restarted.stdout + '\n' + restarted.stderr, encoding='utf8')
            if restarted.returncode == 0 or 'SIM_FULL_RESTART_HOLD' not in restarted.stdout + restarted.stderr:
                raise RuntimeError('Unknown second-ACK Master restarted into production or no restart HOLD')
            restarted_evidence = verify_case(root, scenario)
            if any(restarted_evidence[key] != passed[key] for key in
                   ('claims', 'writes', 'readbacks', 'scannerReceipts')):
                raise RuntimeError('Restart altered original A1/A2/A7 write or ACK evidence')
        result = dict(passed)
        result.update(real_five_distinct_processes=True,
                      firstAndSecondReadbacksFromRealUdp=True,
                      masterRestartGuarded=(scenario == 'ack-second'),
                      simOnly=True, fieldVerified=False)
        (root / 'a5-five-process-extra-verified.json').write_text(
            json.dumps(result, indent=2) + '\n', encoding='utf8')
        print('A5_EXTRA_REAL_FIVE_PROCESS_CLAIM_PASS ' +
              json.dumps(result, separators=(',', ':')), flush=True)
        return result
    except Exception:
        for name, _ in processes:
            s = logtext(name)
            if s:
                print(f'A5_EXTRA_PROCESS_LOG {name}:\n{s[-1550:]}', file=sys.stderr)
        raise
    finally:
        for name, proc in reversed(processes):
            if proc.poll() is None:
                proc.terminate()
        for _, proc in processes:
            try:
                proc.wait(timeout=4)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait(timeout=5)
        for h in handles:
            h.close()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--evidence-root', type=Path, required=True)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--timeout', type=float, default=70)
    args = parser.parse_args(argv)
    if not 15 <= args.timeout <= 180:
        raise ValueError('Bound actual five-process time to 15..180 seconds')
    root = args.evidence_root.resolve()
    source = Path(__file__).resolve().parent.parent
    root.mkdir(parents=True, exist_ok=True)
    for scenario in ('ack-second', 'scan-failed'):
        result = real_five_process_case(source, root / scenario, scenario, args.dotnet,
                                        args.timeout)
        if result['writes'] != 6 or result['readbacks'] != (5 if scenario == 'ack-second' else 6):
            raise RuntimeError('Unexpected actual A1/A2/A7 physical write/readback count')
    return 0


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except Exception as err:
        print('A5_EXTRA_REAL_FIVE_PROCESS_CLAIM_FAIL ' + repr(err), file=sys.stderr)
        raise SystemExit(1)
