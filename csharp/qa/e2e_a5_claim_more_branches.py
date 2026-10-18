#!/usr/bin/env python3
"""Agent05 fifth physical-branch extension: 5-OS-process second ACK loss and scanner failure.

Only drives EXISTING A0 SIM_ONLY e2e_sim_full.py and A2's localhost fault
inputs. It never writes PLC registers itself, creates a synthetic FINS reply or
modifies A0's Host. An abnormal workpiece is *expected* to HOLD; therefore
A0's normal finish-all-items assertion can time out, while A5 independently
validates actual A1/A2/A7 persisted evidence from the still-running Core run.
"""
from __future__ import annotations

import argparse
import os
from pathlib import Path
import subprocess
import sys

from p0_claim_fins_antitamper import verify_case


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--evidence-root', type=Path, required=True)
    parser.add_argument('--dotnet', default='dotnet')
    args = parser.parse_args(argv)
    root = args.evidence_root.resolve()
    source = Path(__file__).resolve().parent.parent
    runner = source / 'scripts/e2e_sim_full.py'
    root.mkdir(parents=True, exist_ok=True)
    for scenario in ('ack-second', 'scan-failed'):
        folder = root / scenario
        if folder.exists():
            raise RuntimeError('Cannot overwrite real five-process evidence: ' + str(folder))
        env = dict(os.environ)
        env.update(AOI_SIM_END_LOSE_ACK='3:1016' if scenario == 'ack-second' else '',
                   AOI_SIM_END_SCAN_FAIL_TOKENS='3' if scenario == 'scan-failed' else '',
                   AOI_SIM_DROP_PEER_AFTER_SLAVE_REGISTRATION='0')
        flags = ['--flip-token', '3'] if scenario == 'ack-second' else []
        command = [sys.executable, str(runner), '--dotnet', args.dotnet,
                   '--legacy-shadow', '--with-images', '--count', '3',
                   '--timeout', '22', '--evidence-root', str(folder), *flags]
        completed = subprocess.run(command, cwd=source, env=env,
                                   capture_output=True, text=True, timeout=108)
        (root / (scenario + '-a5-runner.log')).write_text(
            completed.stdout + '\n--- STDERR ---\n' + completed.stderr,
            encoding='utf8')
        if 'SIM_FULL_FIVE_PROCESSES_LAUNCHED' not in completed.stdout:
            raise RuntimeError('Five real OS processes not launched in ' + scenario + ': ' +
                               completed.stdout[-1200:] + completed.stderr[-2400:])
        # The normal queue completion driver can exit nonzero because item 3
        # must remain HOLD. No core fatal/negative reply can be waved away.
        if completed.returncode == 0 or 'AOI_HOST_FATAL' in completed.stdout + completed.stderr:
            raise RuntimeError('Unsafe normal completion or actual Core fatal in ' + scenario + ': ' +
                               completed.stdout[-3000:] + completed.stderr[-3500:])
        result = verify_case(folder, scenario)
        expected = (5 if scenario == 'ack-second' else 6)
        if result['readbacks'] != expected or result['writes'] != 6:
            raise RuntimeError('A5 incorrect extra-branch actual FINS evidence: ' + str(result))
        print('A5_EXTRA_REAL_FIVE_PROCESS_CLAIM_PASS scenario=' + scenario +
              ' original_A1_A2_A7_wal=1 claims=6 original_udp_writes=6'
              ' readbacks=' + str(result['readbacks']) +
              ' no_synthetic_ack=1 real_plc_writes=0', flush=True)
    return 0


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except Exception as error:
        print('A5_EXTRA_REAL_FIVE_PROCESS_CLAIM_FAIL ' + repr(error), file=sys.stderr)
        raise SystemExit(1)
