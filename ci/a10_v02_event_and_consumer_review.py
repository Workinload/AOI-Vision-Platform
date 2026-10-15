#!/usr/bin/env python3
"""Independent A10 v0.2 six-event JSON Schema positive, negative and mutation oracle.

Reviews declarative C01-DRAFT envelopes only; NO production PLC, NO real WAL,
NO image filesystem evidence, NO persisted-ACK acceptance, NO C01 freeze.
"""
import argparse
import copy
import csv
import hashlib
import importlib.util
import json
import re
import subprocess
import sys
from pathlib import Path
from jsonschema import Draft202012Validator, FormatChecker

KINDS = ("WORKPIECE_RESULT", "ALARM_TRANSITION", "DEPLOYMENT",
         "ASSET", "UI_SNAPSHOT", "AUTH_AUDIT")
OUTCOMES = {"C-01":"A0","C-02":"A3","C-03":"A2","C-04":"A1",
            "C-05":"A1","C-06":"A5","C-07":"A6","C-08":"A6",
            "C-09":"A4","C-10":"A7","C-11":"A8","C-12":"A9"}
def need(condition, label):
    if not condition: raise AssertionError(label)

def main():
    ap=argparse.ArgumentParser()
    ap.add_argument("--repo",required=True,type=Path)
    ap.add_argument("--sha",required=True)
    ap.add_argument("--round",required=True,type=int,choices=[1,2])
    args=ap.parse_args()
    root=args.repo.resolve()
    head=subprocess.check_output(["git","rev-parse","HEAD"],cwd=root,text=True).strip()
    need(head==args.sha,"HEAD_SHA_DRIFT")
    path=root/"contracts/C01/C01_EVENT_VARIANTS_REVIEW_V0_2.schema.json"
    blob=path.read_bytes();schema=json.loads(blob)
    Draft202012Validator.check_schema(schema)
    # jsonschema 'format' is optional. Without the rfc3339 validator,
    # FormatChecker may silently ACCEPT malformed date-time values.
    checker = FormatChecker()
    from jsonschema.exceptions import FormatError
    try:
        checker.check("bad_date_time_not_rfc3339", "date-time")
    except FormatError:
        print("A10_FORMAT_CHECKER_VERIFIED", "bad RFC3339 date rejected")
    else:
        raise AssertionError("MISSING_RFC3339_FORMAT_PLUGIN_FAIL_CLOSED")
    validate=Draft202012Validator(schema,format_checker=checker)
    spec=importlib.util.spec_from_file_location("c01_review_samples",root/"contracts/C01/test_c01_v0_2_review.py")
    mod=importlib.util.module_from_spec(spec);spec.loader.exec_module(mod)
    def succeeds(msg,s=validate):return not list(s.iter_errors(msg))
    checks={"positive":0,"negative":0,"mutation":0,"runtime_gap_confirmed":0}
    def case(label,msg,want):
        actual=succeeds(msg)
        need(actual==want,f"{label}: expected {want} got {actual}")
        key="positive" if want else "negative"
        checks[key]+=1
        print("A10_V02_CASE",label,"ACCEPT" if actual else "REJECT")
    for kind in KINDS:case("six_kind_"+kind,mod.sample(kind),True)
    for k in KINDS[1:]:
        need("WorkpieceId" not in mod.sample(k)["Data"],"fake_workpiece_identity_required")
    base=mod.sample("WORKPIECE_RESULT")
    for s in ("PENDING","RUNNING","ERROR","CANCELLED","TIMEOUT"):
        m=copy.deepcopy(base);m["Data"].update(ProcessingStatus=s,QualityStatus="OK")
        case("no_non_success_OK_"+s,m,False)
    for k,v in (("SchemaVersion","BAD"),("Epoch",0),("Epoch",True),
                ("OccurredUtc","invalid_timestamp"),("PayloadHash","x"*64),
                ("EventKind","UNKNOWN")):
        m=copy.deepcopy(base);m[k]=v;case("bad_common_"+k+"_"+str(v)[:6],m,False)
    m=copy.deepcopy(base);m["Data"]["ChannelId"]="CH12";case("unknown_channel",m,False)
    m=copy.deepcopy(base);m["OtherAttribute"]="ROOT";case("unknown_root_attr",m,False)
    m=mod.sample("ALARM_TRANSITION");m["Data"]["Transition"]="ACK"
    case("ACK_missing_authenticated_receipt",m,False)
    m["Data"].update(ActorId="operator-1",AcknowledgementId="ack-1")
    case("ACK_complete_receipt",m,True)
    m["Data"]["Transition"]="CLEAR"
    case("CLEAR_cannot_use_ACK_as_clear_receipt",m,False)
    m["Data"]["ClearEvidenceId"]="clear-1"
    case("CLEAR_with_distinct_evidence",m,True)
    m=mod.prepared();case("PREPARED_BOTH_structurally_complete",m,True)
    for label,edit in (
        ("missing_core_receipts",lambda v:v["Data"].pop("CoreReadbacks")),
        ("duplicate_core_host",lambda v:v["Data"]["CoreReadbacks"][1].__setitem__("HostId","MASTER")),
        ("missing_vplc_readback",lambda v:v["Data"].pop("VplcReadback")),
        ("plc_marked_non_simulated",lambda v:v["Data"]["VplcReadback"].__setitem__("Simulated",False)),
        ("core_readback_mismatch",lambda v:v["Data"]["CoreReadbacks"][0].__setitem__("ReadBackMatched",False)),
        ("vplc_readback_mismatch",lambda v:v["Data"]["VplcReadback"].__setitem__("ReadBackMatched",False)),
    ):
        bad=copy.deepcopy(m);edit(bad);case(label,bad,False)
    need(mod.readback_verdict(m,current_epoch=2),"runtime readback good sample failed")
    altered=copy.deepcopy(m);altered["Data"]["VplcReadback"]["ParametersHash"]="b"*64
    need(succeeds(altered) and not mod.readback_verdict(altered,current_epoch=2),
         "cross_hash_guard_did_not_fail_closed_in_runtime_oracle")
    checks["runtime_gap_confirmed"]+=1
    print("A10_KNOWN_GAP","SCHEMA_ACCEPTS_DIFFERENT_READBACK_HASH",
          "runtime_mock_rejects_but_no_real_2_CORE_PLC_READBACK")
    need(not mod.readback_verdict(m,current_epoch=3),"stale_epoch_runtime_oracle_failed")
    checks["runtime_gap_confirmed"]+=1
    m=mod.sample("ASSET");m["Data"]["State"]="AVAILABLE"
    case("available_without_verified_metadata",m,False)
    m["Data"].update(StorageUri="assets/parent/raw.png",EncodedSha256="a"*64,
                      DecodedPixelSha256="b"*64,SizeBytes=1024)
    case("asset_full_metadata_only",m,True)
    for label,v in (("path_traversal","assets/../sneak"),
                    ("path_dots","assets/./sneak"),
                    ("path_double_slash","assets//sneak")):
        bad=copy.deepcopy(m);bad["Data"]["StorageUri"]=v;case(label,bad,False)
    need(succeeds(m),"asset schema expected syntax pass")
    checks["runtime_gap_confirmed"]+=1
    print("A10_KNOWN_GAP","AVAILABLE_FIELDS_DO_NOT_PROVE_BYTES_OR_REAL_PIXEL_HASH")
    m=mod.sample("UI_SNAPSHOT");m["Data"].update(Connection="DISCONNECTED",IsLive=True)
    case("UI_disconnect_false_live",m,False)
    m["Data"]["IsLive"]=False;case("UI_disconnect_correct_offline",m,True)
    m=mod.sample("AUTH_AUDIT");m["Data"].update(Action="PLC_WRITE",Decision="ALLOW")
    case("auth_audit_as_record_accepted",m,True)
    need(not mod.default_deny_write("ADMIN","PLC_WRITE",True),
         "SIM_ONLY_mock_exposed_physical_write")
    checks["runtime_gap_confirmed"]+=1
    print("A10_KNOWN_GAP","AUDIT_ALLOW_EVENT_IS_NOT_SERVER_WRITE_PERMISSION")
    need(not any(b["properties"]["EventKind"]["const"]=="PEER_ACK" for b in schema["oneOf"]),
         "new ACK schema requires updated independent assessment")
    checks["runtime_gap_confirmed"]+=1
    print("A10_KNOWN_GAP","NO_DEDICATED_PERSISTED_ACK_EVENT_OR_DISK_COMMIT_PROOF")

    # Deliberate independent schema mutations: a genuinely unsafe modified copy MUST accept a forbidden witness.
    cases=[
        ("drop_workpiece_failure_guard","WORKPIECE_RESULT",mod.sample("WORKPIECE_RESULT"),lambda b:b["properties"]["Data"].pop("allOf")),
        ("drop_alarm_ACK_evidence","ALARM_TRANSITION",mod.sample("ALARM_TRANSITION"),lambda b:b["properties"]["Data"].pop("allOf")),
        ("drop_deploy_prepared_receipts","DEPLOYMENT",mod.prepared(),lambda b:b["properties"]["Data"].pop("allOf")),
        ("drop_asset_available_evidence","ASSET",mod.sample("ASSET"),lambda b:b["properties"]["Data"].pop("allOf")),
        ("drop_ui_disconnect_guard","UI_SNAPSHOT",mod.sample("UI_SNAPSHOT"),lambda b:b["properties"]["Data"].pop("allOf")),
    ]
    for label,kind,witness,edit in cases:
        if kind=="WORKPIECE_RESULT":witness["Data"].update(ProcessingStatus="ERROR",QualityStatus="OK")
        if kind=="ALARM_TRANSITION":witness["Data"]["Transition"]="ACK"
        if kind=="DEPLOYMENT":witness["Data"].pop("VplcReadback")
        if kind=="ASSET":witness["Data"]["State"]="AVAILABLE"
        if kind=="UI_SNAPSHOT":witness["Data"].update(Connection="DISCONNECTED",IsLive=True)
        need(not succeeds(witness),"baseline unsafe "+label)
        weak=copy.deepcopy(schema)
        branch=next(x for x in weak["oneOf"] if x["properties"]["EventKind"]["const"]==kind)
        edit(branch)
        Draft202012Validator.check_schema(weak)
        need(succeeds(witness,Draft202012Validator(weak,format_checker=FormatChecker())),
             "mutation not detected "+label)
        checks["mutation"]+=1
        print("A10_MUTATION_CAUGHT",label,"bad_schema_allows_invalid_witness")

    registry=root/"contracts/C01/CONTRACT_REGISTRY_DRAFT.csv"
    with registry.open(encoding="utf-8",newline="") as f:
        rows=list(csv.DictReader(f))
    reg={x["ContractId"]:x for x in rows}
    need(len(rows)==len(reg)==12 and set(reg)==set(OUTCOMES),"registry 12 IDs")
    need(all(reg[k]["Owner"]==o and reg[k]["Status"]=="PROPOSED_NOT_FROZEN"
             for k,o in OUTCOMES.items()),"registry owner or freeze drift")
    matrix=(root/"contracts/C01/C01_CONSUMER_GATE_MATRIX_20261016.md").read_text(encoding="utf-8")
    entries=re.findall(r"^\| (C-\d\d) \| (A\d+) \| ([^\n]+?) \| ([^\n]+?) \|$",matrix,re.M)
    m={key:(owner,scope,artifact) for key,owner,scope,artifact in entries}
    need(len(entries)==len(m)==12 and set(m)==set(OUTCOMES),"consumer matrix incomplete")
    need(all(m[k][0]==owner for k,owner in OUTCOMES.items()),"consumer owner drift")
    need(all(len(m[k][1])>50 and len(m[k][2])>15 for k in OUTCOMES),
         "consumer blocker text missing")
    need(all(marker in matrix for marker in ("C-06","C-07","C-08","C-10","C-11","C-12",
                                           "ACK","RAW_FRAME","Outbox","14 FIELD","SIM_ONLY")),
         "consumer blockers missing")
    need("NOT_FROZEN" in matrix and "NOT_MERGE_READY" in matrix,
         "consumer matrix falsely promoted")
    ledger=json.loads((root/"docs/agents/TASK_LEDGER.json").read_text())
    need(ledger["items"].get("W004",{}).get("status")!="VERIFIED",
         "W004 prematurely marked verified")
    print("A10_CONSUMER_MATRIX_PASS 12/12 C-01..12 ownership, explicit blockers",
          "NOT_FROZEN=TRUE")
    print("A10_V02_SUMMARY",json.dumps(checks,sort_keys=True),
          "sha="+head,"schema_sha256="+hashlib.sha256(blob).hexdigest(),
          "W004_NOT_VERIFIED","SIM_ONLY")
    return 0

if __name__=="__main__":
    try:sys.exit(main())
    except Exception as exc:
        print("A10_V02_FAIL",type(exc).__name__,str(exc),file=sys.stderr)
        raise
