#!/usr/bin/env python3
"""A10 independent pinned-commit static/negative review; SIM_ONLY, no hardware."""
from __future__ import annotations
import argparse
import copy
import csv
import hashlib
import json
import subprocess
import sys
from pathlib import Path

DEPS = {
    "W083": "W012,W053,W025",
    "W084": "W083,W061",
    "W085": "W084,W053,W055",
    "W086": "W085,W044,W032",
    "W087": "W086,W037,W088",
}
ROUTES = dict(A0="A0", A1="AOI-Agent01", A2="AOI-Agent02",
              A3="AOI-Agent02", A4="AOI-Agent03", A5="AOI-Agent01",
              A6="AOI-Agent03", A7="AOI-Agent06", A8="AOI-Agent04",
              A9="AOI-Agent04", A10="AOI-Agent05")
SIGNAL_FIELDS = ("SignalId", "PLCAddress", "StationId", "HostRole",
                 "Direction", "DataType", "EffectiveWords", "PurposeCN",
                 "RiskNote", "EvidenceSource", "ValidationState",
                 "ConflictGroup", "ProductionWriteEnabled")
GATE_FIELDS = ("GateId", "Topic", "Status", "SourceConflict",
               "RequiredEvidence", "ProposedOwner", "ProductionWriteAllowed",
               "FieldSignoff")


def require(ok, msg):
    if not ok:
        raise AssertionError(msg)


def read_csv(root, path):
    with (root / path).open(encoding="utf-8-sig", newline="") as inp:
        return list(csv.DictReader(inp))


def read_json(root, path):
    return json.loads((root / path).read_text(encoding="utf-8"))


def index(rows, key, count):
    keys = [item[key] for item in rows]
    require(len(keys) == count and len(set(keys)) == count,
            f"incorrect count / duplicate {key} {len(keys)} != {count}")
    return {x[key]: x for x in rows}


def plan_integrity(root):
    manifest = read_json(root, "docs/plan/ORIGINAL_PLAN_SHA256_MANIFEST.json")
    root_dir = root / "docs/plan/v1.1-original"
    paths = {p.relative_to(root_dir).as_posix(): p
             for p in root_dir.rglob("*") if p.is_file()}
    originals = manifest["original_files"]
    require(len(paths) == len(originals) == 55, "frozen PLAN file count")
    for name, expected in originals.items():
        require(name in paths, f"missing immutable PLAN mirror {name}")
        digest = hashlib.sha256(paths[name].read_bytes()).hexdigest()
        require(digest == expected, f"PLAN byte drift {name}")
    wbs = read_csv(root, "docs/plan/v1.1-original/WBS_TASKS_130.csv")
    rq = read_csv(root, "docs/plan/v1.1-original/RQ_114_COVERAGE.csv")
    fg = read_csv(root, "docs/plan/v1.1-original/FIELD_GATES_14.csv")
    index(wbs, "TaskId", 130)
    index(rq, "RequirementId", 114)
    index(fg, "GateId", 14)
    require(len({x["TestCaseIds"] for x in rq}) == 114, "TC drift")
    require(all(x["ProductionWriteAllowed"] == "FALSE" for x in fg),
            "PLAN physical PLC write enabled")
    require(all(x["CurrentExecutionState"] == "NOT_RUN" for x in rq),
            "fabricated functional TC")
    print("PLAN_PROVENANCE_PASS originals=55 WBS=130 RQ=114 TC_NOT_RUN=114 FG_FALSE=14")
    return wbs, fg


def independent_mutations(data, verifier, cases):
    verifier(data)
    count = 0
    for label, edit in cases:
        test_data = copy.deepcopy(data)
        edit(test_data)
        try:
            verifier(test_data)
        except (AssertionError, KeyError, ValueError) as err:
            print("INDEPENDENT_MUTATION_REJECTED", label, repr(str(err)[:160]))
            count += 1
        else:
            raise AssertionError("mutation FALSE-PASS: " + label)
    print("INDEPENDENT_MUTATIONS_PASS", count, "of", len(cases))
    return count


def pr12(root, wbs, fg):
    ownership = read_json(root, "docs/agents/OWNERSHIP.json")
    chat = (root / "docs/agents/CHAT_START_Agent06.md").read_text()
    cr = (root / "docs/reviews/CR-A0-004_AGENT06_A7_ROUTING_20261015.md").read_text()
    ledger = read_json(root, "docs/agents/TASK_LEDGER.json")
    state = dict(own=ownership, wbs=wbs, fg=fg, chat=chat, cr=cr, ledger=ledger)

    def verify(s):
        o, workorder, request = s["own"], s["chat"], s["cr"]
        require(o["chats"].get("AOI-Agent06") == ["A7"], "wrong Agent06 route")
        require(sum("A7" in assigned for assigned in o["chats"].values()) == 1,
                "duplicate A7 assignment")
        require(o["worker_activation"]["AOI-Agent06"]["chat_status"] ==
                "AGENT06_STARTED", "Issue #11 activation fact is stale")
        require(o["worker_activation"]["AOI-Agent06"]["wbs_status"] !=
                "VERIFIED", "A7 fabricated verification")
        for tid, deps in DEPS.items():
            t = next(x for x in s["wbs"] if x["TaskId"] == tid)
            require(t["Owner"] == "A7", "A7 original owner changed " + tid)
            require(t["DependsOn"] == deps, "A7 original depends changed " + tid)
            require(t["Status"] == "NOT_STARTED", "original WBS state promoted")
            require(deps in workorder, "A7 work order dependency missing " + tid)
            require(s["ledger"]["items"].get(tid, {}).get("status") !=
                    "VERIFIED", "A7 ledger bypass " + tid)
        require(s["ledger"]["items"]["W004"]["status"] != "VERIFIED",
                "C01 prematurely frozen")
        require(len(s["fg"]) == 14 and all(
            f["ProductionWriteAllowed"] == "FALSE" for f in s["fg"]),
            "physical write permission")
        require("AOI_SIM_ONLY=ON" in workorder and
                "FAILED_SAFE" in workorder, "safety work order removed")
        require("AGENT06_STARTED" in request and "PR #14" in request,
                "routing CR stale about started chat")
        require("PR #3" in request and "已" in request and "合并" in request,
                "parent PR3 merge status stale")
        require("W083" in request and "W087" in request,
                "A7 WBS absent in CR")
    return independent_mutations(state, verify, [
        ("A7_role_hijacked", lambda s:s["own"]["chats"].__setitem__(
            "AOI-Agent06", ["A8"])),
        ("false_A7_verification", lambda s:s["own"]["worker_activation"][
            "AOI-Agent06"].__setitem__("wbs_status","VERIFIED")),
        ("missing_original_W087_dependency", lambda s:next(x for x in s["wbs"]
            if x["TaskId"] == "W087").__setitem__("DependsOn","W086,W037")),
        ("physical_write_enabled", lambda s:s["fg"][0].__setitem__(
            "ProductionWriteAllowed", "TRUE")),
        ("SIM_ONLY_disabled", lambda s:s.__setitem__("chat",s["chat"].replace(
            "AOI_SIM_ONLY=ON","AOI_SIM_ONLY=OFF"))),
        ("false_W083_ledger",lambda s:s["ledger"]["items"].__setitem__(
            "W083",{"status":"VERIFIED"})),
    ])


def pr27(root, wbs, fg):
    sig = read_csv(root, "docs/reviews/W002_STATIC_FACTS_45.csv")
    src = read_csv(root, "reference/spec_v2_machine_readable/plc_signal_registry.csv")
    gate = read_csv(root, "docs/reviews/W002_FIELD_GATE_OPEN_14.csv")
    original_gate = read_csv(root, "reference/spec_v2_machine_readable/field_verification_gates.csv")
    state = dict(s=sig, orig=src, g=gate, gorig=original_gate, plan_fg=fg)
    def verify(s):
        left,right=index(s["s"],"SignalId",45),index(s["orig"],"SignalId",45)
        require(left.keys()==right.keys(), "PLC signal IDs changed")
        for k in left:
            for field in SIGNAL_FIELDS:
                require(left[k][field]==right[k][field],
                        "PLC fact mismatch "+k+" "+field)
            require(left[k]["ProductionWriteEnabled"]=="FALSE",
                    "source signal production write")
        a,b=index(s["g"],"GateId",14),index(s["gorig"],"GateId",14)
        p=index(s["plan_fg"],"GateId",14)
        require(a.keys()==b.keys()==p.keys(),"FG ID mismatch")
        require(sum(x["Status"]=="BLOCKED" for x in a.values())==7 and
                sum(x["Status"]=="OPEN" for x in a.values())==7,
                "gate blocker count changed")
        for k in a:
            for field in GATE_FIELDS:
                require(a[k][field]==b[k][field],"field gate source drift "+k)
            require(a[k]["ProductionWriteAllowed"]=="FALSE" and
                    p[k]["ProductionWriteAllowed"]=="FALSE",
                    "field write enabled")
            require(not a[k]["FieldSignoff"].strip(),
                    "false on-site signoff")
        for addr in ("D1013","D1000"):
            require(sum(x["PLCAddress"]==addr for x in left.values()) >= 2,
                    "legacy alias silently removed "+addr)
    num=independent_mutations(state,verify,[
        ("change_D_address",lambda d:d["s"][0].__setitem__(
            "PLCAddress","D9999")),
        ("PLC_production_write",lambda d:d["s"][0].__setitem__(
            "ProductionWriteEnabled","TRUE")),
        ("sign_fake_field",lambda d:d["g"][0].__setitem__(
            "FieldSignoff","A10_FAKE")),
        ("erase_legacy_blocker",lambda d:next(x for x in d["g"]
            if x["Status"]=="BLOCKED").__setitem__("Status","OPEN")),
        ("unlock_physical_field",lambda d:d["g"][0].__setitem__(
            "ProductionWriteAllowed","TRUE")),
        ("drop_critical_signal",lambda d:d["s"].pop()),
    ])
    return num


def pr28(root, wbs, fg):
    plan=read_csv(root,"docs/plan/v1.1-original/MODULE_21_OWNERS.csv")
    src=read_csv(root,"reference/spec_v2_machine_readable/module_registry.csv")
    review=read_csv(root,"docs/reviews/W003_MODULE_21_OWNER_REVIEW.csv")
    dag=read_csv(root,"docs/reviews/W003_MODULE_DAG_CANDIDATE.csv")
    state=dict(p=plan,s=src,r=review,d=dag)
    def verify(d):
        a,b,c,edges=(index(d["p"],"ModuleId",21),index(d["s"],"ModuleId",21),
                     index(d["r"],"ModuleId",21),index(d["d"],"ModuleId",21))
        require(a.keys()==b.keys()==c.keys()==edges.keys(),"module drift")
        for key in a:
            for field in ("ModuleId","ModuleName","DevOwner","Process",
                          "MockRequired","Proof"):
                require(c[key][field]==a[key][field],"original owner mutated")
            require(b[key]["ModuleName"]==a[key]["ModuleName"],
                    "v2 module registry mismatch")
            require(c[key]["WorkerChat"]==ROUTES[a[key]["DevOwner"]],
                    "wrong worker route")
            require(edges[key]["State"]=="CANDIDATE_UNAPPROVED" and
                    edges[key]["Meaning"]=="LOGICAL_DATA_DEPENDENCY_NOT_DIRECT_CODE_IMPORT",
                    "false approved module graph")
        require(sum(x["DevOwner"]=="A7" for x in c.values())==2,
                "A7 MOD-13/14 mismatch")
        graph={k:[v for v in item["ProposedConsumesProviderModuleIds"].split(";")
                  if v] for k,item in edges.items()}
        require(sum(map(len,graph.values()))==30,"30-edge DAG drift")
        seen={}
        def walk(node):
            require(seen.get(node)!=1,"cycle in proposed module DAG")
            if seen.get(node)==2:return
            seen[node]=1
            for target in graph[node]:
                require(target in graph and target!=node,"bad module provider")
                walk(target)
            seen[node]=2
        for module in graph:walk(module)
    return independent_mutations(state,verify,[
        ("misassign_A7_owner",lambda d:next(x for x in d["r"]
          if x["DevOwner"]=="A7").__setitem__("DevOwner","A8")),
        ("wrong_A7_chat",lambda d:next(x for x in d["r"]
          if x["DevOwner"]=="A7").__setitem__("WorkerChat","AOI-Agent04")),
        ("duplicate_module_id",lambda d:d["r"][0].__setitem__(
          "ModuleId",d["r"][1]["ModuleId"])),
        ("invented_provider",lambda d:d["d"][0].__setitem__(
          "ProposedConsumesProviderModuleIds","MOD-99")),
        ("self_dependency",lambda d:d["d"][0].__setitem__(
          "ProposedConsumesProviderModuleIds",d["d"][0]["ModuleId"])),
        ("fake_frozen_DAG",lambda d:d["d"][0].__setitem__(
          "State","APPROVED")),
    ])


def pr29(root,wbs,fg):
    registry=read_csv(root,"contracts/C01/CONTRACT_REGISTRY_DRAFT.csv")
    schema=read_json(root,"contracts/C01/CORRELATION_ENVELOPE_DRAFT.schema.json")
    draft=(root/"contracts/C01/C01_FREEZE_CANDIDATE.md").read_text()
    ledger=read_json(root,"docs/agents/TASK_LEDGER.json")
    state=dict(reg=registry,schema=schema,doc=draft,ledger=ledger,fg=fg)
    canonical={"C-07":"A6","C-08":"A6","C-09":"A4",
               "C-10":"A7","C-11":"A8","C-12":"A9"}
    required=("SchemaVersion","MessageId","PayloadHash","WorkpieceId",
              "Epoch","ReleaseId","ChannelId","CaptureId","AttemptNo",
              "ProcessingStatus","QualityStatus")
    def verify(d):
        reg=index(d["reg"],"ContractId",12)
        require(sorted(reg)==[f"C-{i:02d}" for i in range(1,13)],
                "incorrect C01 registry identifiers")
        require(all(x["Status"]=="PROPOSED_NOT_FROZEN"
                    for x in reg.values()),"C01 false freeze")
        for k,v in canonical.items():
            require(reg[k]["Owner"]==v,"incorrect canonical contract owner")
        s=d["schema"]
        require(s["additionalProperties"] is False,
                "schema unknown fields accepted")
        require(set(s["required"])==set(required) and
                set(s["properties"])==set(required),
                "schema identity tuple incomplete")
        require(s["properties"]["SchemaVersion"]["const"]=="C01-DRAFT-0",
                "schema version changed")
        require(s["properties"]["PayloadHash"]["pattern"]=="^[a-f0-9]{64}$",
                "digest policy weakened")
        require(s["properties"]["ChannelId"]["pattern"]=="^CH(0[1-9]|1[01])$",
                "channel safety weakened")
        require("SUCCEEDED" in s["properties"]["ProcessingStatus"]["enum"],
                "processing statuses changed")
        require("NG" in s["properties"]["QualityStatus"]["enum"],
                "quality statuses changed")
        require(d["ledger"]["items"]["W004"]["status"]!="VERIFIED",
                "illegal W004 promotion")
        require(all(x["ProductionWriteAllowed"]=="FALSE" for x in d["fg"]),
                "field write gate bypass")
        require("C01 NOT_FROZEN" in d["doc"] and
                "DRAFT_FOR_CROSS_OWNER_REVIEW" in d["doc"],
                "draft misrepresented as release")
    num=independent_mutations(state,verify,[
        ("canonical_C10_owner_swapped",lambda d:d["reg"][9].__setitem__(
            "Owner","A9")),
        ("false_C01_frozen",lambda d:d["reg"][0].__setitem__(
            "Status","FROZEN")),
        ("remove_WorkpieceId",lambda d:d["schema"]["required"].remove(
            "WorkpieceId")),
        ("loosen_payload_hash",lambda d:d["schema"]["properties"][
            "PayloadHash"].__setitem__("pattern",".*")),
        ("unknown_channel_enabled",lambda d:d["schema"]["properties"][
            "ChannelId"].__setitem__("pattern","^CH.*$")),
        ("fake_W004_verified",lambda d:d["ledger"]["items"]["W004"].__setitem__(
            "status","VERIFIED")),
        ("enable_field_production_write",lambda d:d["fg"][0].__setitem__(
            "ProductionWriteAllowed","TRUE")),
    ])
    # A declared JSON Schema has only independent per-field constraints.
    # The separate Python example validator forbids ERROR+OK, but draft
    # JSON schema does not express this cross-field rule. This is an
    # UNVERIFIED CONTRACT GAP, not license to freeze C01.
    cond=any(k in schema for k in ("if","then","allOf","anyOf","oneOf","not"))
    print("C01_GAP_JSON_SCHEMA_ERROR_PLUS_OK_NOT_DECLARATIVELY_REJECTED",
          not cond, "CONTRACT_NOT_FROZEN")
    require(not cond, "unexpected schema change requires manual review")
    return num


def run_upstream(root, pr):
    commands={
        27:[["docs/reviews/verify_w002.py","--repo-root",".","--negative"]],
        28:[["docs/reviews/verify_w003.py","--repo-root",".","--negative"],
            ["docs/reviews/verify_w003_owner.py","."]],
        29:[["contracts/C01/test_c01_draft_contract.py"]],
    }
    for args in commands.get(pr,[]):
        command=[sys.executable,*args]
        print("UPSTREAM_CMD", " ".join(command),flush=True)
        result=subprocess.run(command,cwd=root,text=True,capture_output=True)
        print(result.stdout)
        print(result.stderr)
        print("UPSTREAM_EXIT",result.returncode)
        require(result.returncode==0,
                "upstream exact-HEAD test failed: "+" ".join(args))


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--repo",type=Path,required=True)
    parser.add_argument("--pr",type=int,choices=(12,27,28,29),required=True)
    parser.add_argument("--sha",required=True)
    parser.add_argument("--round",type=int,choices=(1,2),required=True)
    opt=parser.parse_args();root=opt.repo.resolve()
    actual=subprocess.check_output(["git","rev-parse","HEAD"],
                                   cwd=root,text=True).strip()
    require(actual==opt.sha, f"requested exact SHA {opt.sha}, got {actual}")
    print("A10_ROUND_START",opt.round,"PR",opt.pr,"EXACT_SHA",actual)
    wbs,fg=plan_integrity(root)
    run_upstream(root,opt.pr)
    independent={12:pr12,27:pr27,28:pr28,29:pr29}[opt.pr]
    negatives=independent(root,wbs,fg)
    print("A10_ROUND_PASS",opt.round,"PR",opt.pr,
          "SHA",actual,"INDEPENDENT_NEGATIVES",negatives,
          "SCOPE=STATIC_DRAFT_SAFETY_ONLY_NO_WBS_VERIFICATION")
    return 0

if __name__=="__main__":
    try:
        raise SystemExit(main())
    except Exception as e:
        print("A10_ROUND_FAIL",type(e).__name__,str(e),file=sys.stderr)
        raise
