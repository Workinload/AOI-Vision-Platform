#!/usr/bin/env python3
"""A0 independent inspection of A1 final-output WAL against actual five-process FINS.

Runs real Host/vPLC/Slave/two UI processes using e2e_sim_full.py, then audits
Agent01's irreversible write entitlement events and corresponding raw vPLC
FINS datagrams. Also requires a negative after-applied ACK-loss where the
PLC changed D2099 but A1 journal blocks both replay and second write.
"""
import argparse
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys

from e2e_sim_full import query, scalar


def events_for(root):
    journal = root / 'master/final-plc-output.db'
    assert scalar(journal, 'PRAGMA integrity_check') == 'ok', 'Missing A1 effect WAL'
    rows = query(journal,
                 'SELECT workpiece_id,event_json FROM a1_final_output_events ORDER BY sequence')
    assert rows, 'Host never integrated Agent01 final write journal'
    events = {}
    for workpiece, raw in rows:
        obj = json.loads(raw)
        assert obj['workpieceId'] == workpiece
        events.setdefault(workpiece, []).append(obj)
    items = query(root / 'master/sim-full-cycle.db',
                  'SELECT token,identity_json FROM sim_full_tokens ORDER BY token')
    assert len(items) == 3
    by_token = {token: json.loads(identity)['workpieceId']
                for token, identity in items}
    assert set(by_token.values()) == set(events)
    return by_token, events


def verify_confirmed(root, blocked_token=0):
    ids, found = events_for(root)
    wire = root / 'p0-fins-wire.jsonl'
    assert wire.is_file()
    packets = [json.loads(x) for x in wire.read_text(encoding='utf-8').splitlines()
               if x.strip()]
    for token, item_id in ids.items():
        records = found[item_id]
        kinds = [row['kind'] for row in records]
        assert kinds == ['intentPrepared', 'writeClaimed', 'wireSendObserved',
                         'finsAckObserved', 'readbackConfirmed',
                         'writeClaimed', 'wireSendObserved',
                         'finsAckObserved', 'readbackConfirmed'], (
            token, kinds)
        intent = records[0]['intent']
        assert intent['workpiece']['workpieceId'] == item_id
        assert intent['token'] == token
        expected = ([(2099, 4), (1016, 2)] if token == blocked_token else
                    [(1016, 2), (2099, 2)])
        actual_steps = [(row['wireProof']['dmAddress'],
                         row['wireProof']['value'])
                        for row in records if row['kind'] == 'wireSendObserved']
        assert actual_steps == expected, (token, actual_steps, expected)
        raws = []
        for row in records:
            if row['kind'] != 'wireSendObserved':
                continue
            proof = row['wireProof']
            assert proof['operationId'] and proof['sentUtc']
            assert proof['replyVerified'] is False
            matched = [packet for packet in packets if
                       packet['Token'] == token and packet['IsWrite'] and
                       packet['Address'] == proof['dmAddress'] and
                       packet['Values'] == [proof['value']] and
                       packet['Sid'] == proof['sid'] and
                       packet['Outcome'] == 'OK' and
                       packet['ResponseHex'].startswith('C0')]
            assert len(matched) == 1, ('Missing original raw UDP SID/DM', token, proof)
            raws.append(matched[0]['Sequence'])
        assert len(raws) == 2 and raws[0] < raws[1]
        assert len({row['claimId'] for row in records
                    if row['kind'] == 'writeClaimed'}) == 2
        assert len({row['wireProof']['operationId'] for row in records
                    if row['kind'] == 'wireSendObserved'}) == 2
    masterlog = (root / 'master.log').read_text(encoding='utf-8')
    assert masterlog.count('SIM_A0_JOURNALED_FINAL_OUTPUT_PASS') == 3
    assert scalar(root / 'master/host-peer-interlock.sqlite',
                  'SELECT COUNT(*) FROM host_peer_interlock') == 0
    print('A0_FINAL_JOURNAL_FINS_E2E_PASS items=3 ' +
          'A1_claims=6 raw_sid_matches=6 ack_readback_events=12 ' +
          'reverse_order_fault=' + str(bool(blocked_token)).lower() +
          'replay_writes=0', flush=True)


def verify_ack_unknown(root):
    ids, found = events_for(root)
    faulty_id = ids[3]
    steps = found[faulty_id]
    kinds = [r['kind'] for r in steps]
    assert kinds == ['intentPrepared', 'writeClaimed', 'wireSendObserved',
                     'outcomeUnknown'], kinds
    assert steps[-1]['reasonCode'] == 'FINS_ACK_OR_READBACK_UNKNOWN', steps
    proof = next(r['wireProof'] for r in steps if r['kind'] == 'wireSendObserved')
    assert proof['dmAddress'] == 2099 and proof['value'] == 4
    wire = root / 'p0-fins-wire.jsonl'
    packets = [json.loads(s) for s in wire.read_text(encoding='utf-8').splitlines()
               if s.strip()]
    applied = [p for p in packets if p['Token'] == 3 and p['IsWrite'] and
               p['Address'] == 2099 and p['Values'] == [4] and
               p['Outcome'] == 'INJECTED_TERMINAL_ACK_LOST_AFTER_APPLY']
    assert len(applied) == 1, ('No real after-applied response loss', applied)
    assert applied[0]['Sid'] == proof['sid'] and not applied[0]['ResponseHex']
    assert not any(p['Token'] == 3 and p['IsWrite'] and
                   p['Address'] == 1016 for p in packets), (
                   'Second write sent after unknown first FINS ACK')
    assert not any(p['Token'] == 3 and p['IsWrite'] and
                   p['Address'] == 4003 for p in packets), (
                   'Uncertain workpiece was physically ejected')
    latch = query(root / 'master/host-peer-interlock.sqlite',
                  'SELECT reason_code FROM host_peer_interlock')
    assert latch == [('FINAL_FINS_UNCERTAIN',)], latch
    assert scalar(root / 'master/production.db',
                  "SELECT COUNT(*) FROM workpieces WHERE workpiece_id='" +
                  faulty_id + "' AND final_json IS NOT NULL") == 0
    assert scalar(root / 'master/production.db',
                  'SELECT COUNT(*) FROM workpiece_holds') == 1
    assert scalar(root / 'master/sim-full-cycle.db',
                  "SELECT COUNT(*) FROM sim_full_tokens WHERE phase='exited'") == 2
    master = (root / 'master.log').read_text(encoding='utf-8')
    assert 'SIM_LEGACY_FINS_OUTPUT_UNCERTAIN token=3' in master
    assert 'SIM_LEGACY_ABNORMAL_OUTPUT_UNCERTAIN token=3' in master
    assert 'SIM_FULL_MASTER_OUTPUT_INTERLOCK_HOLD' in master
    assert 'SIM_FULL_MASTER_PRODUCTION_FAILED' not in master
    print('A0_FINAL_ACK_UNKNOWN_E2E_PASS real_after_apply_ack_loss=1 ' +
          'sid_bound=1 WAL_claim_unknown=1 next_fins_write_blocked=1 ' +
          'global_output_interlock=1 no_automatic_retry=1 no_false_ejection=1',
          flush=True)


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--evidence-root')
    p.add_argument('--dotnet', default='dotnet')
    args = p.parse_args()
    source = Path(__file__).resolve().parent.parent
    root = (Path(args.evidence_root).resolve() if args.evidence_root else
            Path(__file__).resolve().parent / '.a0-final-journal-evidence')
    if root.exists() and any(root.iterdir()):
        raise RuntimeError('Output WAL acceptance must use a fresh empty root')
    root.mkdir(parents=True, exist_ok=True)
    runner = source / 'scripts/e2e_sim_full.py'
    for name, flags, should_pass, inject in (
        ('normal', [], True, ''),
        ('mismatch', ['--flip-token', '3'], True, ''),
        ('ack-lost', ['--flip-token', '3'], False, '3:2099')):
        evidence = root / name
        env = dict(os.environ)
        env['AOI_SIM_END_LOSE_ACK'] = inject
        env['AOI_SIM_END_SCAN_FAIL_TOKENS'] = ''
        env['AOI_SIM_DROP_PEER_AFTER_SLAVE_REGISTRATION'] = '0'
        run = subprocess.run([sys.executable, str(runner), '--dotnet',
                              args.dotnet, '--legacy-shadow', '--with-images',
                              '--count', '3', '--timeout',
                              '18' if inject else '65',
                              '--evidence-root', str(evidence), *flags],
                             cwd=source, env=env, capture_output=True, text=True,
                             timeout=92)
        print(run.stdout[-3000:], flush=True)
        if should_pass:
            assert run.returncode == 0, (
                name + ' failed actual five-process Host: ' + run.stderr[-6500:])
            verify_confirmed(evidence, blocked_token=3 if flags else 0)
        else:
            assert run.returncode != 0 and (
                'SIM_FULL_E2E_FAIL TimeoutError' in run.stderr), (
                'Uncertain FINS output was falsely accepted or crashed a Core: ' +
                run.stdout[-2500:] + run.stderr[-4800:])
            verify_ack_unknown(evidence)
    print('A0_FINAL_OUTPUT_THREE_BRANCHES_PASS ' +
          'normal=1 barcode_mismatch=1 ack_after_apply_lost=1 ' +
          'real_five_processes_per_branch=1 no_unsafe_replay=1', flush=True)
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception as exc:
        print('A0_FINAL_JOURNAL_E2E_FAIL ' + repr(exc), file=sys.stderr)
        sys.exit(1)
