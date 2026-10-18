#!/usr/bin/env python3
"""A0 actual five-process negative: invalid installed A6 plugin must HOLD, not GOOD.

Runs a real dual-A6 committed release, nine PNG frame inputs, three Core/vPLC/UI
processes plus two real headless Avalonia OS processes. One immutable CH06 plugin
is valid recipe syntax but intentionally has NO installed A3 processor.
No physical PLC or vendor SDK can be enabled by this acceptance.
"""
import argparse
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys


def sql(dbpath, statement, params=()):
    if not dbpath.is_file():
        raise AssertionError('Missing original SQLite evidence: ' + str(dbpath))
    connection = sqlite3.connect(dbpath)
    try:
        return connection.execute(statement, params).fetchall()
    finally:
        connection.close()


def accept(args):
    root = Path(args.evidence_root).resolve()
    script = Path(__file__).resolve().parent / 'e2e_sim_full.py'
    command = [sys.executable, str(script), '--dotnet', args.dotnet,
               '--legacy-shadow', '--with-images', '--with-product',
               '--with-published-unsupported', '--count', '3',
               '--timeout', '35', '--evidence-root', str(root)]
    finished = subprocess.run(command, cwd=script.parent.parent,
                              env=dict(os.environ), capture_output=True,
                              text=True, timeout=105)
    (root / 'a0-published-unsupported-runner.log').write_text(
        finished.stdout + '\n--STDERR--\n' + finished.stderr,
        encoding='utf8')
    assert finished.returncode == 1 and 'SIM_FULL_FIVE_PROCESSES_LAUNCHED' in finished.stdout, (
        'Real published negative did not reach all 5 OS processes', finished.returncode,
        finished.stdout[-2200:], finished.stderr[-2200:])
    assert 'SIM_FULL_E2E_FAIL TimeoutError' in finished.stderr, (
        'Normal producer crashed instead of safely timing out on held items',
        finished.stderr[-2000:])
    masterlog = (root / 'master.log').read_text(encoding='utf8')
    slavelog = (root / 'slave.log').read_text(encoding='utf8')
    vplclog = (root / 'vplc.log').read_text(encoding='utf8')
    for node, log in [('master', masterlog), ('slave', slavelog), ('vplc', vplclog)]:
        assert 'AOI_HOST_FATAL' not in log and 'SIM_FULL_PRODUCTION_FAILED' not in log, (
            'One unsupported inspection plugin crashed entire ' + node, log[-2500:])
    assert 'SIM_PUBLISHED_CAPTURE_HOLD token=' in masterlog
    assert 'PUBLISHED_PLAN_NOT_READY:NotSupportedException:' in masterlog
    assert 'INSPECTION_PROCESSOR_NOT_READY: vendor.future.not.installed' in masterlog
    assert 'no_camera_or_plc_trigger=1' in masterlog

    store = root / 'master/production.db'
    holds = sql(store,
        "SELECT workpiece_id,reason_code FROM workpiece_holds ORDER BY raised_utc")
    assert holds and all(reason == 'PHYSICAL_INSPECTION_ERROR'
                         for _, reason in holds), holds
    held_ids = {wp for wp, _ in holds}
    assert len(held_ids) == len(holds), 'One WorkpieceId has duplicate safety HOLD'
    registered = sql(store, "SELECT workpiece_id FROM workpieces")
    assert held_ids.issubset({row[0] for row in registered})
    assert sql(store, "SELECT COUNT(*) FROM workpieces WHERE final_json IS NOT NULL")[0][0] == 0
    for workpiece_id in held_ids:
        assert sql(store,
            "SELECT COUNT(*) FROM channel_results WHERE workpiece_id=? AND channel_id='CH06'",
            (workpiece_id,))[0][0] == 0, workpiece_id
    # Original UDP receiver-side bytes, not Core-predicted ACK/PLC memory.
    wirepath = root / 'p0-fins-wire.jsonl'
    records = [json.loads(line) for line in wirepath.read_text(encoding='utf8').splitlines()
               if line.strip()]
    assert records, 'No real FINS evidence'
    assert not any(r['IsWrite'] and r['Address'] == 1011 and
                   r.get('Values') in ([2], [3]) for r in records), (
                       'Noninstalled CH06 plugin unexpectedly received D1011 CamDone')
    assert not any(r['IsWrite'] and r['Address'] in (1016, 2099) and
                   r.get('Values') in ([1], [2], [3], [4]) for r in records), (
                       'Physical final output appeared for an uninspected/held workpiece')
    assert 'SIM_FULL_EXITED token=' not in masterlog, 'Held item physically left the vPLC'
    print('A0_UNSUPPORTED_A6_PLUGIN_REAL_FIVE_PROCESS_HOLD_PASS ' +
          'held_workpieces=' + str(len(held_ids)) +
          ' original_raw_FINS_CH06_CamDone=0' +
          ' original_raw_FINS_final_writes=0' +
          ' persisted_final_good=0 durable_workpiece_hold=1' +
          ' master_slave_vplc_alive_before_controlled_stop=1' +
          ' no_camera_soft_trigger=1 real_plc_writes=0',
          flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--evidence-root', required=True)
    args = parser.parse_args()
    try:
        accept(args)
        return 0
    except Exception as exc:
        print('A0_UNSUPPORTED_A6_PLUGIN_HOLD_FAIL ' + repr(exc),
              file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
