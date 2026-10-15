#!/usr/bin/env python3
"""SIM_ONLY A1/A5 consumer oracle for A0 C01 draft; NOT a frozen contract test.
Usage: python3 c01_schema_consumer_audit.py /path/to/CORRELATION_ENVELOPE_DRAFT.schema.json [smoke|audit]
Exit: smoke=0 when positive guards match; audit=2 when freeze blockers found;
      3 when jsonschema is not installed; 1 if test itself fails.
"""
import copy
import json
import sys
from pathlib import Path

try:
    import jsonschema
except ImportError:
    print('NOT_VERIFIED: needs jsonschema Draft202012Validator; no silently skipped tests')
    sys.exit(3)

BASE = {
    'SchemaVersion':'C01-DRAFT-0', 'MessageId':'event-1','PayloadHash':'a'*64,
    'WorkpieceId':'wp-1','Epoch':2,'ReleaseId':'rel-1','ChannelId':'CH08',
    'CaptureId':'cap-1','AttemptNo':1,'ProcessingStatus':'SUCCEEDED','QualityStatus':'NG'}


def main(schema_file, mode):
    schema = json.loads(Path(schema_file).read_text(encoding='utf-8'))
    jsonschema.Draft202012Validator.check_schema(schema)
    validator = jsonschema.Draft202012Validator(schema)
    def accepted(changes=None, removed=None):
        item=dict(BASE)
        item.update(changes or {})
        if removed: item.pop(removed)
        return validator.is_valid(item)
    smoke = {
        'valid_envelope_accepted': accepted(),
        'unknown_schema_rejected': not accepted({'SchemaVersion':'C02'}),
        'unknown_field_rejected': not accepted({'Extra':'injected'}),
        'missing_workpiece_rejected': not accepted(removed='WorkpieceId'),
        'wrong_channel_rejected': not accepted({'ChannelId':'CH12'}),
        'empty_capture_rejected': not accepted({'CaptureId':''}),
        'negative_epoch_rejected': not accepted({'Epoch':-1}),
        'boolean_epoch_rejected': not accepted({'Epoch':True}),
    }
    for name,ok in smoke.items(): print('SMOKE',name,'PASS' if ok else 'FAIL')
    if not all(smoke.values()):
        print('SCHEMA_SMOKE=FAIL')
        return 1
    print('SCHEMA_SMOKE=PASS',len(smoke),'tests')
    if mode == 'smoke':return 0
    # Expected to be rejected by the FINAL typed message consumer, not by current draft.
    probes={
        'Epoch_zero_invalid_result': accepted({'Epoch':0}),
        'AttemptNo_zero_invalid_result': accepted({'AttemptNo':0}),
        'Epoch_overflows_uint64': accepted({'Epoch':2**64}),
        'AttemptNo_overflows_uint32': accepted({'AttemptNo':2**32}),
        'WorkpieceId_whitespace_only': accepted({'WorkpieceId':' '}),
        'ERROR_must_never_be_OK': accepted({'ProcessingStatus':'ERROR','QualityStatus':'OK'}),
        'PENDING_must_not_be_final_NG': accepted({'ProcessingStatus':'PENDING','QualityStatus':'NG'}),
        'SUCCEEDED_UNDECIDED_needs_explicit_policy': accepted({'QualityStatus':'UNDECIDED'}),
    }
    props=set(schema.get('properties',{}))
    missing_required={
        'message_kind_discriminator_missing': 'MessageKind' not in props,
        'source_host_identity_missing':'SourceHostId' not in props,
        'destination_host_identity_missing':'DestinationHostId' not in props,
        'dedup_order_sequence_missing':'SequenceNo' not in props,
        'trace_id_missing':'TraceId' not in props,
        'persisted_ack_message_binding_missing':'AckedMessageId' not in props,
        'persisted_ack_receipt_missing':'DurableReceiptId' not in props,
        'receiver_commit_proof_missing':'ReceiverCommitId' not in props,
        'peer_clock_quality_missing':'ClockQuality' not in props,
        'final_outcome_missing':'FinalOutcome' not in props,
        'origin_authority_missing':'ResultAuthority' not in props,
    }
    issues={**probes, **missing_required}
    for name,found in issues.items():
        print('HOLD' if found else 'RESOLVED',name)
    count=sum(issues.values())
    print('CONSUMER_BLOCKERS=',count, 'total_probes=',len(issues))
    print('CONSUMER_REVIEW=HOLD (schema semantics require A0 proposal + A10)')
    return 2 if count else 0

if __name__=='__main__':
    if len(sys.argv)!=3 or sys.argv[2] not in ('audit','smoke'):
        print(__doc__)
        sys.exit(1)
    sys.exit(main(sys.argv[1],sys.argv[2]))
