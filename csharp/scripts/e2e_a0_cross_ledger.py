#!/usr/bin/env python3
"""A0 read-only operator reconciliation of actual A1/A7 five-process evidence.

Runs after e2e_a0_final_output_journal.py and never starts a PLC or Core.
The Host must classify genuine two-step ACK+readback, barcode HOLD output,
and actual applied-but-lost ACK without changing ANY original incident WAL.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys


def originals(root):
    evidence = {}
    for file in ('final-plc-output.db', 'timing-events.sqlite'):
        for suffix in ('', '-wal', '-shm'):
            path = root / 'master' / (file + suffix)
            if path.is_file():
                evidence[path.name] = hashlib.sha256(path.read_bytes()).hexdigest()
    assert 'final-plc-output.db' in evidence and 'timing-events.sqlite' in evidence
    return evidence


def host_json(dotnet, dll, root, args, prefix):
    result = subprocess.run(
        [dotnet, str(dll), *args], cwd=root.parent,
        env=dict(os.environ), capture_output=True, text=True, timeout=20)
    lines = [line[len(prefix):] for line in result.stdout.splitlines()
             if line.startswith(prefix)]
    assert len(lines) == 1, ('No singular Host forensic result', args,
                             result.stdout[-1800:], result.stderr[-1600:])
    return result.returncode, json.loads(lines[0])


def each_case(dotnet, host, root, name):
    before = originals(root)
    rc, stopped = host_json(dotnet, host, root,
        ['inspect', '--data', str(root)], 'AOI_RECOVERY_INSPECT_JSON=')
    assert rc == 0
    workpieces = {int(p['physicalToken']): p for p in stopped['workpieces']}
    assert sorted(workpieces) == [1, 2, 3]
    assert stopped['realPlcWritesEnabled'] is False
    assert stopped['automaticProductionResumeAllowed'] is False
    assert originals(root) == before, 'A0 preliminary inspector modified originals'
    for token, workpiece in workpieces.items():
        id = workpiece['workpieceId']
        code, result = host_json(dotnet, host, root,
            ['cross-ledger', '--data', str(root), '--workpiece', id],
            'AOI_CROSS_LEDGER_INSPECT_JSON=')
        expect_unknown = name == 'ack-lost' and token == 3
        state = result['status'].upper()
        assert result['workpieceId'] == id
        assert result['physicalToken'] == token
        assert result['simOnly'] is True
        assert result['realPlcWriteAuthorized'] is False
        assert result['physicalEjectionVerified'] is False
        assert result['traceId'] and result['releaseId'] and result['epoch'] >= 1
        assert len(result['steps']) == 2, result
        if expect_unknown:
            assert code == 3 and state == 'ACK_UNKNOWN', (name, token, result)
            assert result['requiresManualReconciliation'] is True
            assert result['steps'][0]['bothLedgersConfirmed'] is False
            assert result['steps'][1]['claimId'] is None
            assert any('ACK_UNKNOWN' in f['code'] for f in result['findings'])
        else:
            assert code == 0 and state == 'CONFIRMED', (name, token, result)
            assert result['requiresManualReconciliation'] is False
            assert not result['findings']
            for step in result['steps']:
                assert step['bothLedgersConfirmed'] is True
                assert step['claimId']
                assert step['authoritySid'] == step['auditSid']
                for key in ('rawRequestSha256', 'rawResponseSha256',
                            'readRequestSha256', 'readResponseSha256'):
                    value = step[key]
                    assert value and len(value) == 64 and all(
                        ch in '0123456789abcdef' for ch in value), (key, step)
                assert step['readbackValue'] == step['value']
        assert originals(root) == before, (
            name, token, 'Cross-ledger operator query modified live or stopped evidence')
        print('A0_REAL_CROSS_LEDGER_OPERATOR_PASS scenario=' + name +
              ' token=' + str(token) + ' status=' + state +
              ' no_raw_evidence_mutation=1 no_live_plc=1 no_restart=1', flush=True)

    # One A7-consistent STOPPED snapshot must reconcile ALL original three
    # immutable WorkpieceIds, not de-duplicate repeated barcodes or omit an
    # unknown-ACK item. A partially returned/truncated batch is never SUCCESS.
    code, batch = host_json(dotnet, host, root,
        ['cross-ledger-batch', '--data', str(root)],
        'AOI_CROSS_LEDGER_BATCH_JSON=')
    records = batch['workpieces']
    by_id = {x['workpieceId']: x for x in records}
    assert len(records) == len(by_id) == 3, (name, batch)
    assert set(by_id) == {x['workpieceId'] for x in workpieces.values()}
    assert batch['truncated'] is False
    for token, item in workpieces.items():
        report = by_id[item['workpieceId']]
        expect_unknown = name == 'ack-lost' and token == 3
        assert report['physicalToken'] == token
        assert report['status'].upper() == ('ACK_UNKNOWN' if expect_unknown
                                            else 'CONFIRMED'), (name, token, report)
        assert report['simOnly'] is True
        assert report['realPlcWriteAuthorized'] is False
        assert report['physicalEjectionVerified'] is False
        assert report['requiresManualReconciliation'] is expect_unknown
    assert batch['overallStatus'].upper() == (
        'ACK_UNKNOWN' if name == 'ack-lost' else 'CONFIRMED'), batch
    assert code == (3 if name == 'ack-lost' else 0), (name, code, batch)
    assert originals(root) == before

    code, truncated = host_json(dotnet, host, root,
        ['cross-ledger-batch', '--data', str(root), '--limit', '1'],
        'AOI_CROSS_LEDGER_BATCH_JSON=')
    assert code == 3 and truncated['truncated'] is True, truncated
    assert len(truncated['workpieces']) == 1
    assert truncated['workpieces'][0]['workpieceId'] in by_id
    assert originals(root) == before
    print('A0_REAL_CROSS_LEDGER_BATCH_PASS scenario=' + name +
          ' original_workpieces=3 limit1_truncated=1 '
          'duplicate_barcode_never_used_as_identity=1 no_false_full_success=1 '
          'original_DB_WAL_SHA_unchanged=1', flush=True)

    # Unknown WorkpieceId must never receive another item's successful claim.
    unknown = '00000000-0000-4000-8000-000000000001'
    assert all(row['workpieceId'] != unknown for row in workpieces.values())
    rc, missing = host_json(dotnet, host, root,
        ['cross-ledger', '--data', str(root), '--workpiece', unknown],
        'AOI_CROSS_LEDGER_INSPECT_JSON=')
    assert rc == 3 and missing['status'].upper() == 'AUDIT_MISSING', missing
    assert missing['requiresManualReconciliation'] is True
    assert not missing['steps'] and not missing['realPlcWriteAuthorized']
    assert originals(root) == before


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--evidence-root', required=True)
    opts = parser.parse_args()
    root = Path(opts.evidence_root).resolve()
    host = Path(__file__).resolve().parents[1] / 'src/AOI.Host/bin/Debug/net10.0/AOI.Host.dll'
    assert host.is_file(), 'Build .NET10 Host first'
    for name in ('normal', 'mismatch', 'ack-lost'):
        case = root / name
        assert (case / 'master/production.db').is_file(), name
        each_case(opts.dotnet, host, case, name)
    print('A0_CROSS_LEDGER_THREE_REAL_FIVE_PROCESS_BRANCHES_PASS ' +
          'two_actual_sids_and_raw_sha_confirmed=1 ' +
          'unconfirmed_first_ACK_never_forged=1 '
          'unknown_workpiece_fails_closed=1 '
          'original_sqlite_WAL_SHA_unchanged=1 '
          'no_hardware_or_auto_resume=1 batch_limit1_truncated_rejected=1 ' +
          'one_stopped_A1_A7_snapshot_per_batch=1', flush=True)


if __name__ == '__main__':
    try:
        main()
    except Exception as ex:
        print('A0_CROSS_LEDGER_OPERATOR_FAIL ' + repr(ex), file=sys.stderr)
        sys.exit(1)
