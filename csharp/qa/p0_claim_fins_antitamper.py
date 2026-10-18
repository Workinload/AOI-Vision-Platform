#!/usr/bin/env python3
"""Agent05: independent, read-only A1 claim / A2 actual FINS / A7 SHA auditor.

A0 generates the ORIGINAL real five-process evidence. This module never changes
that evidence and never contacts a PLC. --tamper-all works on new SQLite backups
and JSONL copies, and requires each single alteration to be rejected.
"""
from __future__ import annotations

import argparse
from contextlib import closing
import hashlib
import json
from pathlib import Path
import shutil
import sqlite3
import sys
import tempfile
import uuid

from p0_fins_timeline_audit import AuditFailure, demand, load_records, parse_trace


class EvidenceError(AuditFailure):
    pass


def require(ok, reason, detail):
    if not ok:
        raise EvidenceError(reason, detail)


def read_sql(path: Path, statement: str, args=()):
    require(path.is_file(), 'NOT_VERIFIED', f'Original SQLite WAL unavailable: {path}')
    with closing(sqlite3.connect(path.as_uri() + '?mode=ro', uri=True, timeout=5)) as connection:
        return connection.execute(statement, args).fetchall()


def guid(value, role):
    try:
        parsed = uuid.UUID(value)
    except (ValueError, TypeError, AttributeError) as exc:
        raise EvidenceError('INVALID_IDENTITY', f'{role} is not a UUID: {value!r}') from exc
    require(parsed.int != 0, 'INVALID_IDENTITY', f'{role} empty UUID')
    return str(parsed)


def sha(hex_text):
    require(isinstance(hex_text, str) and bool(hex_text), 'MISSING_RAW_BYTES', 'Actual FINS hex absent')
    try:
        return hashlib.sha256(bytes.fromhex(hex_text)).hexdigest()
    except ValueError as exc:
        raise EvidenceError('INVALID_RAW_BYTES', 'FINS original hex is malformed') from exc


def exactly(items, code, description):
    require(len(items) == 1, code, f'{description}: expected one, observed {len(items)}')
    return items[0]


def ensure_sequential(phases, required, token):
    require(phases == required, 'A7_PHASE_SEQUENCE', f'workpiece token={token}: {phases} != {required}')


def verify_case(root: Path, scenario: str) -> dict:
    """Require three actual Core identities, 3 A7 intents and one matching A1 claim per UDP write.

    This is a fail-closed *consistency* proof, not an anti-collusion signature:
    someone with write access to all three original SQLite/JSONL sources can
    reforge them together. Physical vPLC receive-side capture is necessary.
    """
    require(scenario in ('normal', 'mismatch', 'ack-lost'), 'BAD_SCENARIO', scenario)
    source = root / 'p0-fins-wire.jsonl'
    rows = load_records(source)
    wire = parse_trace(rows)  # Independently decode packet bytes, SID, routing and reply.
    require(len(rows) == len(wire), 'WIRE_CAPTURE', 'Original packet count differs')
    require(all(isinstance(x, dict) for x in rows), 'WIRE_CAPTURE', 'Original packet is not an object')
    wal = root / 'master/final-plc-output.db'
    a7 = root / 'master/timing-events.sqlite'
    ledger = root / 'master/sim-full-cycle.db'
    tokens = read_sql(ledger, 'SELECT token, identity_json FROM sim_full_tokens ORDER BY token')
    require(len(tokens) == 3 and [x[0] for x in tokens] == [1, 2, 3],
            'CORE_IDENTITY', 'Three separate production tokens required')
    identities = {token: json.loads(body) for token, body in tokens}
    workpieces = {k: guid(v.get('workpieceId'), 'Core WorkpieceId') for k, v in identities.items()}
    require(len(set(workpieces.values())) == 3, 'CORE_IDENTITY', 'WorkpieceId reused across physical tokens')

    intents = read_sql(a7, '''SELECT attempt_id,workpiece_id,trace_id,epoch,release_id,
                         token,scenario,disposition FROM final_output_intents ORDER BY token''')
    require(len(intents) == 3 and [x[5] for x in intents] == [1, 2, 3],
            'A7_INTENTS', 'Exactly one A7 output intent per physical token required')
    all_a1 = read_sql(wal, '''SELECT sequence,intent_id,workpiece_id,event_json
                             FROM a1_final_output_events ORDER BY sequence''')
    require(bool(all_a1), 'NOT_VERIFIED', 'A1 original final-output WAL missing events')
    by_item = {item: [] for item in workpieces.values()}
    for sequence, intent_id, item, payload in all_a1:
        require(item in by_item, 'A1_WRONG_WORKPIECE', f'A1 event references unknown workpiece {item}')
        event = json.loads(payload)
        require(event['sequence'] == sequence and
                guid(event['workpieceId'], 'A1 event workpiece') == item and
                guid(event['intentId'], 'A1 intentId') == guid(intent_id, 'A1 table intentId'),
                'A1_ROW_TAMPERED', 'A1 event JSON differs from independent row identity/sequence')
        by_item[item].append(event)
    counts = {'writes': 0, 'readbacks': 0, 'scans': 0, 'claims': 0}
    seen_claims: set[str] = set()
    consumed_wire_writes: set[int] = set()

    for attempt, item, trace, epoch, release, token, a7case, disposition in intents:
        item = guid(item, 'A7 workpiece')
        require(item == workpieces[token], 'A7_WRONG_WORKPIECE', f'A7 token={token} not Core identity')
        real_id = identities[token]
        require(guid(trace, 'A7 TraceId') == guid(real_id.get('traceId'), 'Core TraceId')
                and epoch == real_id['epoch'] and release == real_id['releaseId'],
                'A7_WRONG_RELEASE', f'A7 token={token} TraceId/epoch/release is altered')
        expected_case = 'BarcodeMismatch' if token == 3 and scenario != 'normal' else 'Normal'
        require(a7case == expected_case and disposition == 'Reject',
                'A7_SCENARIO', f'token={token} incorrect scenario/disposition')
        expected = ([(2099, 4), (1016, 2)] if expected_case == 'BarcodeMismatch'
                    else [(1016, 2), (2099, 2)])
        required_steps = 1 if token == 3 and scenario == 'ack-lost' else 2
        events = read_sql(a7, '''SELECT phase,step_number,operation_id,fins_sid,dm_address,
                                value,request_sha256,response_sha256,readback_value
                                FROM final_output_audit_events WHERE attempt_id=?
                                ORDER BY sequence''', (attempt,))
        require(bool(events), 'A7_NO_EVENTS', f'token={token} missing A7 raw-wire evidence')
        a1 = by_item[item]
        require(bool(a1), 'A1_NO_EVENTS', f'token={token} missing independent A1 intent/claims')
        intent = a1[0]
        require(intent['kind'] == 'intentPrepared' and
                intent['intent']['token'] == token and
                guid(intent['intent']['workpiece']['workpieceId'], 'A1 intent workpiece') == item and
                guid(intent['intent']['workpiece']['traceId'], 'A1 intent TraceId') == guid(trace, 'A7 trace'),
                'A1_INTENT_MISMATCH', f'token={token}: intent differs from Core/A7 identity')
        require(intent['intent']['workpiece']['epoch'] == epoch and
                intent['intent']['workpiece']['releaseId'] == release,
                'A1_INTENT_MISMATCH', f'token={token}: A1 release/epoch altered')
        a1claims = [x for x in a1 if x['kind'] == 'writeClaimed']
        require(len(a1claims) == required_steps,
                'A1_CLAIM_COUNT', f'token={token}: expected {required_steps} original one-time claims')
        all_steps = [x for x in events if x[1] > 0]
        require(set(x[1] for x in all_steps) == set(range(1, required_steps + 1)),
                'A7_EXTRA_STEP', f'token={token}: A7 contains missing or forbidden step')
        scan = exactly([x for x in events if x[0] == 'ScannerRequestVerified'],
                       'A7_SCANNER', f'token={token} observed external D2099 request')
        require(scan[1] == 0 and scan[4:6] == (2099, 1),
                'A7_SCANNER', f'token={token}: scanner did not prove D2099=1')
        observed_scan = [i for i, (p, w) in enumerate(zip(rows, wire))
                         if w.token == token and not w.write and w.address == 2099
                         and w.sid == scan[3] and w.ok and w.reply_words == (1,)
                         and sha(p['RequestHex']) == scan[6] and
                         sha(p['ResponseHex']) == scan[7]]
        exactly(observed_scan, 'RAW_SCANNER_MISSING', f'token={token} true PLC scanner receipt')
        counts['scans'] += 1

        for step in range(1, required_steps + 1):
            address, expected_value = expected[step - 1]
            step_events = [x for x in events if x[1] == step]
            prepared = exactly([x for x in step_events if x[0] == 'StepPrepared'],
                               'A7_PREPARED', f'token={token} step={step} prepared')
            sent = exactly([x for x in step_events if x[0] == 'WireSent'],
                           'A7_WIRE_SENT', f'token={token} step={step} transmitted')
            claimed = a1claims[step - 1]
            claim_id = guid(claimed.get('claimId'), 'A1 ClaimId')
            require(claim_id not in seen_claims, 'DUPLICATE_CLAIM', 'ClaimId reused by separate operations')
            seen_claims.add(claim_id)
            require(claimed['stepIndex'] == step - 1 and
                    guid(prepared[2], 'A7 OperationId') == claim_id and
                    guid(sent[2], 'A7 WireSent OperationId') == claim_id and
                    (prepared[4], prepared[5]) == (address, expected_value),
                    'CLAIM_ID_MISMATCH', f'token={token} step={step} A1/A7 operation or DM/value changed')
            observed = [(i, p, w) for i, (p, w) in enumerate(zip(rows, wire))
                        if w.token == token and w.write and w.address == address]
            packet_index, packet, packet_wire = exactly(observed, 'RAW_OUTPUT_DUPLICATED',
                f'token={token} D{address} must have only one actual UDP WRITE')
            require(packet_wire.words == (expected_value,) and
                    packet_wire.sid == prepared[3] and
                    sha(packet['RequestHex']) == prepared[6],
                    'RAW_WRITE_TAMPERED', f'token={token} step={step} original datagram differs')
            require(packet_index not in consumed_wire_writes, 'RAW_OUTPUT_DUPLICATED',
                    'One received UDP datagram cannot satisfy two one-time Claims')
            consumed_wire_writes.add(packet_index)
            require([x[0] for x in step_events[:2]] == ['StepPrepared', 'WireSent'],
                    'A7_PHASE_SEQUENCE', 'Before ACK must PREPARE -> actually SEND')
            wire_proofs = [e for e in a1 if e['kind'] == 'wireSendObserved' and
                           e.get('claimId') == claim_id]
            a1_sent = exactly(wire_proofs, 'A1_WIRE_CLAIM', f'token={token} claim={claim_id}')
            proof = a1_sent['wireProof']
            require(guid(proof['operationId'], 'A1 UDP OperationId') == claim_id and
                    proof['sid'] == packet_wire.sid and
                    (proof['dmAddress'], proof['value']) == (address, expected_value),
                    'A1_WIRE_CLAIM', f'token={token} A1 ClaimId/SID/address/value was changed')
            counts['claims'] += 1
            counts['writes'] += 1
            if scenario == 'ack-lost' and token == 3:
                ensure_sequential([x[0] for x in step_events],
                                  ['StepPrepared', 'WireSent', 'AckUnknown'], token)
                require(packet_wire.outcome == 'INJECTED_TERMINAL_ACK_LOST_AFTER_APPLY'
                        and not packet_wire.response_seen and packet['ResponseHex'] == '' and
                        (prepared[7] is None or prepared[7] == '') and
                        [x['kind'] for x in a1] ==
                        ['intentPrepared', 'writeClaimed', 'wireSendObserved', 'outcomeUnknown'],
                        'FALSE_ACK_UNKNOWN', 'Unknown ACK incorrectly treated as confirmed')
                continue
            ensure_sequential([x[0] for x in step_events],
                              ['StepPrepared', 'WireSent', 'AckAccepted', 'ReadbackVerified'], token)
            require(packet_wire.ok and sha(packet['ResponseHex']) == step_events[2][7] and
                    step_events[2][2] == claim_id and step_events[2][6] == prepared[6],
                    'FALSE_ACK', f'token={token} A7 ACK not provable from actual UDP response')
            a1_ack = exactly([x for x in a1 if x['kind'] == 'finsAckObserved'
                              and x.get('claimId') == claim_id],
                             'A1_FALSE_ACK', f'token={token} A1 ACK')
            require(a1_ack['wireProof']['sid'] == packet_wire.sid and
                    a1_ack['wireProof']['replyVerified'] is True,
                    'A1_FALSE_ACK', 'A1 falsely marked unverified FINS reply')
            read = step_events[3]
            # Other external PLC engineering/diagnostic readers may poll D2099
            # between the output and the ORIGINAL A7-confirmed readback,
            # especially on Windows. They are NOT the A7 physical readback.
            # Require the exact independently persisted A7 SID, raw request SHA,
            # response SHA and decoded value. A later unrelated matching DM
            # value cannot be used in place of this one-time operation.
            actual_reads = [(i, p, w) for i, (p, w) in enumerate(zip(rows, wire))
                            if w.token == token and not w.write and w.address == address
                            and w.sid == read[3] and w.seq > packet_wire.seq]
            first_index, first_raw, first_wire = exactly(
                actual_reads, 'READBACK_DELETED',
                f'token={token} step={step} actual A7 SID={read[3]} readback packet')
            require(first_wire.ok and first_wire.reply_words == (expected_value,) and
                    sha(first_raw['RequestHex']) == read[6]
                    and sha(first_raw['ResponseHex']) == read[7] and
                    read[8] == expected_value and first_index > packet_index,
                    'READBACK_TAMPERED', f'token={token} step={step} A7-SID-bound real UDP readback invalid')
            a1_read = exactly([x for x in a1 if x['kind'] == 'readbackConfirmed' and
                               x.get('claimId') == claim_id], 'A1_READBACK',
                              f'token={token} confirmed readback')
            require(a1_read['readbackValue'] == expected_value, 'A1_READBACK',
                    'A1 confirmed result differs from UDP readback')
            if step < required_steps:
                upcoming = [w.seq for w in wire if w.token == token and w.write and
                            w.address == expected[step][0]]
                require(bool(upcoming) and first_wire.seq < min(upcoming),
                        'READBACK_AFTER_NEXT_SEND', f'token={token} step2 sent before readback')
            counts['readbacks'] += 1
        require(len([x for x in a1 if x['kind'] == 'wireSendObserved']) == required_steps,
                'A1_EXTRA_WIRE', f'token={token} unauthorized extra physical write')
        require(len([x for x in events if x[0] == 'AckAccepted']) ==
                (required_steps - (1 if scenario == 'ack-lost' and token == 3 else 0)),
                'A7_FALSE_ACK', f'token={token} extra accepted ACK')

    physical_writes = [i for i, w in enumerate(wire) if w.write and w.address in (1016, 2099)]
    require(set(physical_writes) == consumed_wire_writes, 'UNCLAIMED_FINAL_WRITE',
            'One or more received FINS output writes have no matching immutable A1 ClaimId')
    expected_count = 5 if scenario == 'ack-lost' else 6
    require(counts['writes'] == expected_count and
            counts['readbacks'] == expected_count - (1 if scenario == 'ack-lost' else 0) and
            counts['scans'] == 3 and counts['claims'] == expected_count,
            'INCOMPLETE_PROOF', str(counts))
    return {'status': 'PASS', 'scenario': scenario, 'writes': counts['writes'],
            'readbacks': counts['readbacks'], 'claims': counts['claims'],
            'scannerReceipts': counts['scans'], 'realPacketCount': len(rows),
            'fieldVerified': False, 'simOnly': True}


def snapshot_original(source: Path, target: Path):
    """Take consistent read-only SQLite backups, NOT raw byte copies of live WAL files."""
    require(not target.exists(), 'SNAPSHOT_DEST_EXISTS', f'Refusing overwrite {target}')
    target.mkdir(parents=True)
    originals = ['p0-fins-wire.jsonl', 'master/final-plc-output.db',
                 'master/timing-events.sqlite', 'master/sim-full-cycle.db']
    for relative in originals:
        src = source / relative
        dst = target / relative
        require(src.is_file(), 'NOT_VERIFIED', f'Original real five-process evidence missing {src}')
        dst.parent.mkdir(exist_ok=True, parents=True)
        if relative.endswith(('.db', '.sqlite')):
            with closing(sqlite3.connect(src.as_uri() + '?mode=ro', uri=True)) as original:
                with closing(sqlite3.connect(dst)) as clone:
                    original.backup(clone)
        else:
            shutil.copyfile(src, dst)


def rewrite_raw(folder: Path, modify):
    src = folder / 'p0-fins-wire.jsonl'
    lines = [json.loads(x) for x in src.read_text(encoding='utf8').splitlines() if x.strip()]
    modify(lines)
    src.write_text(''.join(json.dumps(x, separators=(',', ':')) + '\n' for x in lines),
                   encoding='utf8')


def first_output(records):
    return exactly([x for x in records if x['Token'] == 1 and x['IsWrite'] and
                    x['Address'] == 1016], 'TAMPER_FIXTURE', 'original token1 D1016 write')


def mutate_case(folder: Path, mutation: str):
    if mutation in ('request-hex', 'response-hex', 'sid', 'request-sid', 'reply-sid', 'duplicate-write', 'drop-readback'):
        def change(lines):
            first = first_output(lines)
            if mutation in ('request-hex', 'response-hex'):
                key = 'RequestHex' if mutation == 'request-hex' else 'ResponseHex'
                raw = bytearray.fromhex(first[key])
                require(len(raw) > 13, 'TAMPER_FIXTURE', key)
                raw[-1] ^= 1  # one actual byte changed, keep all metadata and other sources.
                first[key] = raw.hex().upper()
            elif mutation == 'sid':
                first['Sid'] = (first['Sid'] + 1) % 256
            elif mutation in ('request-sid', 'reply-sid'):
                key = 'RequestHex' if mutation == 'request-sid' else 'ResponseHex'
                raw = bytearray.fromhex(first[key])
                raw[9] ^= 1  # Tamper actual SID byte while recorded metadata stays unchanged.
                first[key] = raw.hex().upper()
            elif mutation == 'duplicate-write':
                copied = dict(first)
                copied['Sequence'] = lines[-1]['Sequence'] + 1
                copied['MonotonicTimestamp'] = lines[-1]['MonotonicTimestamp'] + 1
                copied['ReceivedUtc'] = lines[-1]['ReceivedUtc']
                lines.append(copied)
            else:
                position = lines.index(first)
                # Remove the actual A7 SID-bound readback, not an unrelated
                # engineering poll of the same DM address.
                a7path = folder / 'master/timing-events.sqlite'
                sid_rows = read_sql(a7path, """SELECT e.fins_sid
                    FROM final_output_audit_events e
                    JOIN final_output_intents i ON e.attempt_id=i.attempt_id
                    WHERE i.token=1 AND e.step_number=1 AND e.phase='ReadbackVerified'""")
                actual_sid = exactly(sid_rows, 'TAMPER_FIXTURE', 'real A7 readback SID')[0]
                idx = next(i for i in range(position + 1, len(lines)) if
                           lines[i]['Token'] == 1 and not lines[i]['IsWrite'] and
                           lines[i]['Address'] == 1016 and lines[i]['Sid'] == actual_sid)
                lines.pop(idx)
                # Rebase only metadata sequence: do not forge a replacement reply.
                for i, entry in enumerate(lines):
                    entry['Sequence'] = i + 1
        rewrite_raw(folder, change)
    elif mutation in ('claim-id', 'unknown-claim-id', 'workpiece-id',
                      'a7-forged-ack', 'unknown-ack-promoted'):
        if mutation in ('claim-id', 'unknown-claim-id'):
            path = folder / 'master/final-plc-output.db'
            target_item = None
            if mutation == 'unknown-claim-id':
                identity = exactly(read_sql(folder / 'master/sim-full-cycle.db',
                    'SELECT identity_json FROM sim_full_tokens WHERE token=3'),
                    'TAMPER_FIXTURE', 'actual unknown-ACK token3')[0]
                target_item = json.loads(identity)['workpieceId']
            with closing(sqlite3.connect(path)) as conn, conn:
                events = conn.execute('SELECT sequence,event_json FROM a1_final_output_events').fetchall()
                sequence, event = next((n, json.loads(body)) for n, body in events
                    if json.loads(body)['kind'] == 'writeClaimed' and
                    (target_item is None or json.loads(body)['workpieceId'] == target_item))
                event['claimId'] = str(uuid.uuid4())
                conn.execute('UPDATE a1_final_output_events SET event_json=? WHERE sequence=?',
                             (json.dumps(event), sequence))
        elif mutation == 'workpiece-id':
            path = folder / 'master/timing-events.sqlite'
            with closing(sqlite3.connect(path)) as conn, conn:
                conn.execute('UPDATE final_output_intents SET workpiece_id=? WHERE token=1',
                             (str(uuid.uuid4()),))
        elif mutation == 'a7-forged-ack':
            path = folder / 'master/timing-events.sqlite'
            with closing(sqlite3.connect(path)) as conn, conn:
                attempt = conn.execute('SELECT attempt_id FROM final_output_intents WHERE token=1').fetchone()[0]
                step = conn.execute("SELECT sequence FROM final_output_audit_events WHERE attempt_id=? AND phase='AckAccepted' ORDER BY sequence LIMIT 1", (attempt,)).fetchone()[0]
                conn.execute('UPDATE final_output_audit_events SET response_sha256=? WHERE sequence=?',
                             ('0' * 64, step))
        else:
            # Directly flip the A7 classification for the REAL lost ACK, with
            # NO network reply invented: the independent chain must reject it.
            path = folder / 'master/timing-events.sqlite'
            with closing(sqlite3.connect(path)) as conn, conn:
                attempt = conn.execute('SELECT attempt_id FROM final_output_intents WHERE token=3').fetchone()[0]
                changed = conn.execute("UPDATE final_output_audit_events SET phase='AckAccepted' WHERE attempt_id=? AND step_number=1 AND phase='AckUnknown'", (attempt,))
                require(changed.rowcount == 1, 'TAMPER_FIXTURE', 'expected one original A7 AckUnknown')
    else:
        raise EvidenceError('UNKNOWN_TAMPER', mutation)


ATTACKS = ('request-hex', 'response-hex', 'sid', 'request-sid', 'reply-sid',
           'duplicate-write', 'drop-readback', 'claim-id', 'workpiece-id', 'a7-forged-ack')
UNKNOWN_ATTACKS = ('unknown-ack-promoted', 'unknown-claim-id')


def tamper_all(root: Path, scenario: str, location: Path) -> dict:
    """Apply scenario-specific single-field changes ONLY to copied real evidence."""
    require(not location.exists(), 'SNAPSHOT_DEST_EXISTS', f'Cannot overwrite {location}')
    location.mkdir(parents=True)
    results = {}
    attacks = ATTACKS + (UNKNOWN_ATTACKS if scenario == 'ack-lost' else ())
    for mutation in attacks:
        folder = location / mutation
        snapshot_original(root, folder)
        mutate_case(folder, mutation)
        try:
            verify_case(folder, scenario)
        except AuditFailure as error:
            # "NOT_VERIFIED" from missing evidence is correctly rejected, but
            # the mutation must not turn the detector into a silent PASS.
            results[mutation] = error.code
            print(f'A5_CLAIM_TAMPER_REJECTED scenario={scenario} attack={mutation} '
                  f'reason={error.code}', flush=True)
        else:
            raise EvidenceError('FALSE_ACK_ACCEPTED',
                                f'Original {scenario} data was changed ({mutation}), but verifier returned PASS')
    require(len(results) == len(attacks), 'TAMPER_MATRIX_INCOMPLETE', str(results))
    return {'status': 'PASS_TAMPER_REJECTED', 'scenario': scenario,
            'negativeAttacks': results, 'simOnly': True, 'fieldVerified': False}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--evidence-root', required=True, type=Path,
                        help='parent containing normal/mismatch/ack-lost real five-process folders')
    parser.add_argument('--tamper-all', action='store_true')
    parser.add_argument('--tamper-root', type=Path)
    args = parser.parse_args(argv)
    try:
        root = args.evidence_root.resolve()
        report = {}
        for scenario in ('normal', 'mismatch', 'ack-lost'):
            source = root / scenario
            result = verify_case(source, scenario)
            print('A5_REAL_CLAIM_WIRE_PASS ' + json.dumps(result, sort_keys=True), flush=True)
            report[scenario] = result
            if args.tamper_all:
                target = (args.tamper_root.resolve() if args.tamper_root else
                          root.parent / (root.name + '-tampered')) / scenario
                report[scenario]['tampering'] = tamper_all(source, scenario, target)
        if args.tamper_all:
            print('A5_REAL_TAMPER_MATRIX_PASS scenarios=3 cases=32 '
                  'raw_bytes=1 SID=1 A1_claimId=1 A7_SHA=1 WorkpieceId=1 '
                  'readback=1 duplicate_final_write=1 synthetic_ACK_rejected=1', flush=True)
        return 0
    except AuditFailure as exc:
        print(f'A5_REAL_CLAIM_WIRE_FAIL reason={exc.code} {exc}', file=sys.stderr)
        return 3 if exc.code == 'NOT_VERIFIED' else 1
    except (OSError, sqlite3.Error, ValueError, KeyError, IndexError, TypeError) as exc:
        print(f'A5_REAL_CLAIM_WIRE_FAIL reason=UNTRUSTED_EVIDENCE {exc!r}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
