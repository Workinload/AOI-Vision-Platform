#!/usr/bin/env python3
"""W005 static RQ/WBS/TC provenance: NOT functional TC execution."""
import csv,copy,json,sys
from pathlib import Path

def load(root):
    def rows(name):
        with (root/name).open(encoding="utf-8-sig",newline="") as f:
            return list(csv.DictReader(f))
    return rows("WBS_TASKS_130.csv"),rows("RQ_114_COVERAGE.csv"),rows("SCENARIO_MATRIX.csv")

def audit(w,r,s):
    errors=[];tasks={x["TaskId"]:x for x in w}
    if len(tasks)!=len(w) or len(w)!=130:errors.append("WBS_COUNT")
    if len(r)!=114 or len({x["RequirementId"] for x in r})!=114:errors.append("RQ_COUNT")
    if len({x["TestCaseIds"] for x in r})!=114:errors.append("TC_COUNT")
    if sum(x["Priority"]=="P0" for x in r)!=78 or sum(x["Priority"]=="P1" for x in r)!=36:errors.append("PRIORITY_SPLIT")
    if len(s)!=52:errors.append("SCENARIO_COUNT")
    if any(x["PrimaryWBS"] not in tasks for x in r):errors.append("BROKEN_RQ_WBS")
    if any(any(dep not in tasks for dep in filter(None,x["SupportingWBS"].split(","))) for x in r):errors.append("BROKEN_SUPPORT")
    if any(any(dep not in tasks for dep in filter(None,x["DependsOn"].split(","))) for x in w):errors.append("BROKEN_DEPS")
    if any(x["CurrentExecutionState"]!="NOT_RUN" for x in r):errors.append("FAKE_TC_EXECUTION")
    if any(x["ExecutionStatus"]!="NOT_RUN" for x in s):errors.append("FAKE_SCENARIO_EXECUTION")
    return errors

def main():
    root=Path(sys.argv[1]) if len(sys.argv)>1 else Path("docs/plan/v1.1-original")
    w,r,s=load(root);baseline=audit(w,r,s)
    mutations=[
      ("bad_primary_wbs","r",0,"PrimaryWBS","W999","BROKEN_RQ_WBS"),
      ("fake_tc_pass","r",0,"CurrentExecutionState","PASS","FAKE_TC_EXECUTION"),
      ("unknown_dependency","w",0,"DependsOn","W999","BROKEN_DEPS"),
      ("fake_scenario_pass","s",0,"ExecutionStatus","PASS","FAKE_SCENARIO_EXECUTION"),
      ("duplicate_tc","r",1,"TestCaseIds",r[0]["TestCaseIds"],"TC_COUNT"),
    ]
    tests=[]
    for name,which,index,field,bad,expected in mutations:
        a,b,c=copy.deepcopy((w,r,s))
        {"w":a,"r":b,"s":c}[which][index][field]=bad
        result=audit(a,b,c);tests.append({"test":name,"caught":expected in result,"errors":result})
    out={"mode":"STATIC_ONLY_NO_TC_EXECUTED","RQ":len(r),"TC":len({x["TestCaseIds"] for x in r}),
         "TC_NOT_EXECUTED":sum(x["CurrentExecutionState"]=="NOT_RUN" for x in r),
         "SCENARIOS_NOT_EXECUTED":sum(x["ExecutionStatus"]=="NOT_RUN" for x in s),
         "baseline_errors":baseline,"negative_tests":tests}
    print(json.dumps(out,ensure_ascii=False,indent=2))
    return 1 if baseline or not all(x["caught"] for x in tests) else 0

if __name__=="__main__":
    sys.exit(main())
