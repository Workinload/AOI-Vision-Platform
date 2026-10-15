#!/usr/bin/env python3
"""Original 114 TC / 52 SC source-locked execution inventory, never blanket PASS.

The default output is NOT_RUN and FIELD_PENDING for ALL formal cases. This
program audits source consistency; it does not execute domain functionality.
"""
from __future__ import annotations
import argparse
import copy
import csv
import hashlib
import json
import re
import sys
from pathlib import Path


class InvalidPlan(ValueError):
    pass


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def require(condition,msg):
    if not condition:raise InvalidPlan(msg)


def csv_rows(path):
    with Path(path).open(encoding="utf-8-sig",newline="") as f:
        return list(csv.DictReader(f))


def comma_ids(s):
    return [x.strip() for x in re.split(r"[,;]",s) if x.strip()]


def audit(wbs,rq,sc):
    tasks={r["TaskId"] for r in wbs}
    req={r["RequirementId"] for r in rq}
    cases=[r["TestCaseIds"] for r in rq]
    scenarios={r["ScenarioId"] for r in sc}
    require(len(wbs)==len(tasks)==130,"WBS_130_UNIQUE")
    require(len(rq)==len(req)==len(set(cases))==114,"RQ_TC_114_UNIQUE")
    require(len(sc)==len(scenarios)==52,"SC_52_UNIQUE")
    require(sum(r["Priority"]=="P0" for r in rq)==78,"P0_78_EXPECTED")
    require(sum(r["Priority"]=="P1" for r in rq)==36,"P1_36_EXPECTED")
    require(all(re.fullmatch(r"RQ-\d{3}",x) for x in req),"BAD_RQ_ID")
    require(all(re.fullmatch(r"TC-\d{3}",x) for x in cases),"BAD_TC_ID")
    require(all(re.fullmatch(r"SC-\d{3}",x) for x in scenarios),"BAD_SC_ID")
    require(all(r["CurrentExecutionState"]=="NOT_RUN" for r in rq),"FAKE_ORIGINAL_TC_PASS")
    require(all(r["ExecutionStatus"]=="NOT_RUN" for r in sc),"FAKE_ORIGINAL_SC_PASS")
    require(all(r["PrimaryWBS"] in tasks for r in rq),"ORPHAN_RQ_WBS")
    require(all(all(t in tasks for t in comma_ids(r["SupportingWBS"])) for r in rq),"ORPHAN_SUPPORT_WBS")
    require(all(all(t in tasks for t in comma_ids(r["RelatedTaskIds"])) for r in sc),"ORPHAN_SC_WBS")
    require(all(all(t in tasks for t in comma_ids(r["DependsOn"])) for r in wbs),"ORPHAN_TASK_DEPENDENCY")
    require(all(r["Owner"] and r["OriginalReleaseGate"] for r in rq),"MISSING_TC_OWNER_GATE")
    require(all(r["MandatoryEvidence"] and r["ExpectedMachineAssertion"] for r in sc),"MISSING_SC_EVIDENCE")
    # The initial PLAN snapshot is a historical immutable CSV, not today's WBS ledger.
    return {"wbs":len(wbs),"requirements":len(rq),"cases":len(cases),
            "scenarios":len(sc),"p0":78,"p1":36}


def negative_oracles(wbs,rq,sc):
    cases=[
        ("duplicate_tc",lambda a:a[1][1].__setitem__("TestCaseIds",a[1][0]["TestCaseIds"])),
        ("duplicate_rq",lambda a:a[1][1].__setitem__("RequirementId",a[1][0]["RequirementId"])),
        ("missing_primary_wbs",lambda a:a[1][0].__setitem__("PrimaryWBS","W999")),
        ("missing_support_wbs",lambda a:a[1][0].__setitem__("SupportingWBS","W999")),
        ("missing_task_dependency",lambda a:a[0][0].__setitem__("DependsOn","W999")),
        ("false_TC_result_in_original",lambda a:a[1][0].__setitem__("CurrentExecutionState","SIM_PASS")),
        ("duplicate_scenario",lambda a:a[2][1].__setitem__("ScenarioId",a[2][0]["ScenarioId"])),
        ("false_SC_result_in_original",lambda a:a[2][0].__setitem__("ExecutionStatus","SIM_PASS")),
        ("scenario_without_evidence",lambda a:a[2][0].__setitem__("MandatoryEvidence","")),
        ("priority_corruption",lambda a:a[1][0].__setitem__("Priority","P9")),
    ]
    passed=[]
    for label,edit in cases:
        clone=copy.deepcopy([wbs,rq,sc]);edit(clone)
        try:audit(*clone)
        except InvalidPlan as error:passed.append({"mutation":label,"rejected":True,"reason":str(error)})
        else:raise InvalidPlan("NEGATIVE_FALSE_ACCEPT:"+label)
    return passed


def create(root,output,selftest):
    wbs_path=root/"WBS_TASKS_130.csv"
    rq_path=root/"RQ_114_COVERAGE.csv"
    sc_path=root/"SCENARIO_MATRIX.csv"
    wbs,rq,sc=csv_rows(wbs_path),csv_rows(rq_path),csv_rows(sc_path)
    counters=audit(wbs,rq,sc)
    neg=negative_oracles(wbs,rq,sc) if selftest else []
    tests=[{"tc_id":r["TestCaseIds"],"requirement_id":r["RequirementId"],
            "priority":r["Priority"],"owner":r["Owner"],
            "primary_wbs":r["PrimaryWBS"],"support_wbs":comma_ids(r["SupportingWBS"]),
            "assertion":r["Requirement"],"simulation":"NOT_RUN",
            "field":"FIELD_PENDING","evidence":[],"exit_code":None,
            "origin_gate":r["OriginalReleaseGate"]} for r in rq]
    scenarios=[{"sc_id":r["ScenarioId"],"group":r["Group"],
                "owner":r["Owner"],"required_tasks":comma_ids(r["RelatedTaskIds"]),
                "trigger":r["FaultOrTrigger"],"assertion":r["ExpectedMachineAssertion"],
                "mandatory_evidence":r["MandatoryEvidence"],
                "simulation":"NOT_RUN","field":"FIELD_PENDING","evidence":[],"exit_code":None} for r in sc]
    data={"schema":"aoi.a10.execution-inventory.v1", "scope":"FORMAL_PLAN_NOT_EXECUTED",
          "source":"original read-only PLAN snapshots", "historical_plan_wbs_not_current_main_ledger":True,
          "wbs_verified_updates_authorized":False,"formal_tc_pass":0,"formal_sc_pass":0,
          "counts":counters, "negative_mutations":neg,
          "locked_source_sha256":{x.name:sha(x) for x in (wbs_path,rq_path,sc_path)},
          "tc":tests,"sc":scenarios}
    if output:
        if output.is_symlink():raise InvalidPlan("SYMLINK_OUTPUT_FORBIDDEN")
        if output.resolve().is_relative_to(root.resolve()):raise InvalidPlan("ORIGINAL_PLAN_READ_ONLY")
        output.parent.mkdir(parents=True,exist_ok=True)
        output.write_text(json.dumps(data,indent=2,ensure_ascii=False)+"\n",encoding="utf-8")
    print(json.dumps({"status":"PASS","scope":data["scope"],
                      "tc_not_run":len(tests),"sc_not_run":len(scenarios),
                      "field_pending_tc":len(tests),"field_pending_sc":len(scenarios),
                      "mutations_rejected":len(neg),"expected_mutations":10 if selftest else 0,
                      "inventory_sha256":sha(output) if output else None},ensure_ascii=False))
    return 0


def main():
    p=argparse.ArgumentParser()
    p.add_argument("--plan-dir",type=Path,default=Path("reference/plan_v1.1"))
    p.add_argument("--output",type=Path)
    p.add_argument("--selftest",action="store_true")
    a=p.parse_args()
    try:return create(a.plan_dir.resolve(),a.output,a.selftest)
    except (InvalidPlan,FileNotFoundError,KeyError) as e:
        print("INVENTORY_FAIL_CLOSED",str(e),file=sys.stderr);return 2

if __name__=="__main__":sys.exit(main())
