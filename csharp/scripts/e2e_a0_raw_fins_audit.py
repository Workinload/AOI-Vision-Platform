#!/usr/bin/env python3
"""A0 cross-ledger proof of actually transmitted FINS packets (A1+A2+A7).

Run AFTER existing e2e_a0_final_output_journal.py has created three true
five-OS-process runs. A7 must contain the *actual* immutable request/response
bytes' SHA256, same SID and same A1 persisted one-time ClaimId. The separate
external vPLC raw wire recorder is the independent packet witness.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sqlite3
import sys


def sql(path, query, args=()):
    assert path.is_file(), 'Missing original evidence database ' + str(path)
    with sqlite3.connect('file:' + path.as_posix() + '?mode=ro', uri=True) as db:
        return db.execute(query, args).fetchall()


def sha(hexstring):
    return hashlib.sha256(bytes.fromhex(hexstring)).hexdigest()


def verify(path, scenario, ack_lost=False):
    a1 = path / 'master/final-plc-output.db'
    a7 = path / 'master/timing-events.sqlite'
    ledger = path / 'master/sim-full-cycle.db'
    rawfile = path / 'p0-fins-wire.jsonl'
    assert rawfile.is_file()
    raw = [json.loads(line) for line in rawfile.read_text(encoding='utf-8').splitlines()
           if line.strip()]
    ledger_ids = {
        token: json.loads(identity)['workpieceId']
        for token, identity in sql(ledger,
            'SELECT token,identity_json FROM sim_full_tokens ORDER BY token')
    }
    intents = sql(a7, '''SELECT attempt_id,workpiece_id,token,scenario,disposition
                         FROM final_output_intents ORDER BY token''')
    assert len(intents) == 3, ('Master A7 did not persist all output attempts', intents)
    assert [x[2] for x in intents] == [1, 2, 3]

    total_write_proofs = 0
    total_readback_proofs = 0
    for attempt_id, workpiece, token, a7scenario, disposition in intents:
        assert ledger_ids[token] == workpiece
        expected_case = 'BarcodeMismatch' if scenario == 'mismatch' and token == 3 else 'Normal'
        assert a7scenario == expected_case
        assert disposition == 'Reject'
        records = sql(a7, """
            SELECT phase,step_number,operation_id,fins_sid,dm_address,value,
                   request_sha256,response_sha256,readback_value
              FROM final_output_audit_events WHERE attempt_id=?
             ORDER BY sequence
            """, (attempt_id,))
        assert records, ('A7 journal lacks actual network observations', token)
        phases = [x[0] for x in records]
        expected = ([(2099, 4), (1016, 2)] if expected_case == 'BarcodeMismatch'
                    else [(1016, 2), (2099, 2)])

        scanner = [x for x in records if x[0] == 'ScannerRequestVerified']
        assert len(scanner) == 1 and scanner[0][4:6] == (2099, 1), (token, scanner)
        scan = scanner[0]
        external_scans = [p for p in raw if p['Token'] == token and
                          p['Address'] == 2099 and not p['IsWrite'] and
                          p['Sid'] == scan[3] and p['Outcome'] == 'OK' and
                          p['RequestHex'] and p['ResponseHex']]
        assert len(external_scans) == 1, (token, 'missing independent scanner read')
        assert sha(external_scans[0]['RequestHex']) == scan[6]
        assert sha(external_scans[0]['ResponseHex']) == scan[7]

        a1claims = [
            json.loads(row[0])
            for row in sql(a1, '''SELECT event_json FROM a1_final_output_events
                                  WHERE workpiece_id=? ORDER BY sequence''', (workpiece,))
            if json.loads(row[0])['kind'] == 'writeClaimed'
        ]
        expect_steps = 1 if ack_lost and token == 3 else 2
        assert len(a1claims) == expect_steps, (token, 'wrong one-time claims', a1claims)
        for step in range(1, expect_steps + 1):
            addr, value = expected[step-1]
            events = [x for x in records if x[1] == step]
            prepared = [x for x in events if x[0] == 'StepPrepared']
            sent = [x for x in events if x[0] == 'WireSent']
            assert len(prepared) == len(sent) == 1, (token, step, events)
            prepared = prepared[0]
            # A1 ClaimId (exclusive output authority) is the very same
            # immutable A2 network OperationId and A7 prepared operation.
            claim_id = a1claims[step-1]['claimId']
            assert prepared[2] == claim_id and sent[0][2] == claim_id
            assert (prepared[4], prepared[5]) == (addr, value), (token, step)
            packets = [p for p in raw if p['Token'] == token and p['IsWrite'] and
                       p['Address'] == addr and p['Values'] == [value] and
                       p['Sid'] == prepared[3] and p['RequestHex'] and
                       sha(p['RequestHex']) == prepared[6]]
            assert len(packets) == 1, (
                token, step, 'A7 fabricated or duplicated actual UDP write')
            packet = packets[0]
            total_write_proofs += 1
            if ack_lost and token == 3 and step == 1:
                assert packet['Outcome'] == 'INJECTED_TERMINAL_ACK_LOST_AFTER_APPLY'
                assert not packet['ResponseHex']
                assert sum(x[0] == 'AckUnknown' for x in events) == 1
                assert not any(x[0] in ('AckAccepted', 'ReadbackVerified') for x in events)
            else:
                assert packet['Outcome'] == 'OK' and packet['ResponseHex']
                ack = [x for x in events if x[0] == 'AckAccepted']
                readbacks = [x for x in events if x[0] == 'ReadbackVerified']
                assert len(ack) == len(readbacks) == 1, (token, step, phases)
                assert ack[0][2] == claim_id
                assert ack[0][6] == prepared[6]
                assert ack[0][7] == sha(packet['ResponseHex'])
                read = readbacks[0]
                independent = [p for p in raw if p['Token'] == token and
                               p['Address'] == addr and not p['IsWrite'] and
                               p['Sid'] == read[3] and p['Outcome'] == 'OK' and
                               p['RequestHex'] and p['ResponseHex']]
                assert len(independent) == 1, (token, step, 'missing true readback')
                assert read[6] == sha(independent[0]['RequestHex'])
                assert read[7] == sha(independent[0]['ResponseHex'])
                assert read[8] == value
                total_readback_proofs += 1
        if ack_lost and token == 3:
            assert not any(row[1] == 2 for row in records), (
                'A7 shows forbidden second send after UNKNOWN ACK')
        else:
            assert phases.count('AckAccepted') == phases.count('ReadbackVerified') == 2

    expected_count = 5 if ack_lost else 6
    assert total_write_proofs == expected_count
    assert total_readback_proofs == expected_count - (1 if ack_lost else 0)
    print('A0_A1_A2_A7_ACTUAL_RAW_FINS_PASS scenario=' + scenario +
          ' ack_lost=' + str(ack_lost).lower() +
          ' actual_unique_writes=' + str(total_write_proofs) +
          ' independent_real_readbacks=' + str(total_readback_proofs) +
          ' scanner_real_request_receipts=3' +
          ' raw_request_and_reply_sha_verified=1' +
          ' single_A1_claim_per_UDP_send=1 no_fabricated_ack=1', flush=True)


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--evidence-root', required=True)
    args = p.parse_args()
    root = Path(args.evidence_root).resolve()
    for name in ('normal', 'mismatch', 'ack-lost'):
        verify(root / name, 'mismatch' if name != 'normal' else 'normal',
               ack_lost=name == 'ack-lost')
    print('A0_A1_A2_A7_THREE_FIVE_PROCESS_BRANCHES_PASS ' +
          'normal=1 barcode_mismatch=1 after_applied_first_ack_lost=1 '
          'actual_bytes_not_reconstructed=1 no_auto_replay=1', flush=True)


if __name__ == '__main__':
    try:
        main()
    except Exception as ex:
        print('A0_A1_A2_A7_RAW_WIRE_AUDIT_FAIL ' + repr(ex), file=sys.stderr)
        sys.exit(1)
