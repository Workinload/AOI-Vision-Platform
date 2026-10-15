#!/usr/bin/env python3
"""W003 independently reproducible 21-module ownership and 30-edge DAG check.

This validates static evidence and proposed routing, NOT task acceptance.
"""
import argparse
import copy
import csv
import hashlib
import io
import json
from pathlib import Path

ROUTES = {"A0":"A0", "A1":"AOI-Agent01", "A2":"AOI-Agent02",
          "A3":"AOI-Agent02", "A4":"AOI-Agent03", "A5":"AOI-Agent01",
          "A6":"AOI-Agent03", "A7":"AOI-Agent06", "A8":"AOI-Agent04",
          "A9":"AOI-Agent04", "A10":"AOI-Agent05"}

def read_csv(path):
    return list(csv.DictReader(io.StringIO(path.read_bytes().decode("utf-8-sig"))))

def index(rows,key,count):
    keys=[r[key] for r in rows]
    assert len(keys)==count and len(set(keys))==count, (key,"wrong count or duplicate")
    return {r[key]:r for r in rows}

def verify(plan,registry,proposed,dag):
    a=index(plan,"ModuleId",21)
    b=index(registry,"ModuleId",21)
    c=index(proposed,"ModuleId",21)
    d=index(dag,"ModuleId",21)
    assert set(a)==set(b)==set(c)==set(d), "module ID mismatch"
    for k,orig in a.items():
        row=c[k]; actual=b[k]
        for key in ("ModuleId","ModuleName","DevOwner","Process","MockRequired","Proof"):
            assert row[key]==orig[key], ("PLAN module drift",k,key)
        assert actual["ModuleName"]==orig["ModuleName"], ("v2 registry drift",k)
        assert row["WorkerChat"]==ROUTES[orig["DevOwner"]], ("incorrect worker assignment",k)
        if orig["DevOwner"]=="A7":
            assert row["Decision"]=="USER_APPROVED_CHAT_STARTED_ROUTING_PR12_A10_PASS_MERGED", "invalid A7 approval claim"
        assert d[k]["State"]=="CANDIDATE_UNAPPROVED", "DAG falsely frozen"
        assert d[k]["Meaning"]=="LOGICAL_DATA_DEPENDENCY_NOT_DIRECT_CODE_IMPORT", "DAG implementation claim"
    edges={k:[x for x in row["ProposedConsumesProviderModuleIds"].split(";") if x] for k,row in d.items()}
    assert sum(map(len,edges.values()))==30, "candidate dependency edge drift"
    for k,linked in edges.items():
        assert len(linked)==len(set(linked)), ("duplicate edges",k)
        for v in linked:
            assert v in a and v!=k, ("missing/self provider",k,v)
    state={}
    def visit(node):
        if state.get(node)==1:raise AssertionError("module dependency cycle " + node)
        if state.get(node)==2:return
        state[node]=1
        for provider in edges[node]:visit(provider)
        state[node]=2
    for k in a:visit(k)
    assert sum(r["DevOwner"]=="A7" for r in proposed)==2, "A7 module count"
    return {"plan_modules":21,"v2_registry_modules":21,"worker_routes":21,"A7_modules":2,"logical_edges_candidate":30,"cycles":0,"routing_status":"AGENT06_STARTED_PR12_A10_PASS_MERGED","wbs_status":"NOT_VERIFIED"}

def mutation_negatives(arrays):
    tests=[
       ("duplicate_module",lambda a:a[2].__setitem__(0,dict(a[2][0],ModuleId=a[2][1]["ModuleId"]))),
       ("wrong_owner",lambda a:a[2][0].__setitem__("DevOwner","A7")),
       ("wrong_chat",lambda a:a[2][0].__setitem__("WorkerChat","AOI-Agent06")),
       ("invented_provider",lambda a:a[3][0].__setitem__("ProposedConsumesProviderModuleIds","MOD-99")),
       ("dependency_cycle",lambda a:a[3][0].__setitem__("ProposedConsumesProviderModuleIds","MOD-05")),
       ("false_dag_freeze",lambda a:a[3][0].__setitem__("State","APPROVED")),
       ("false_a7_status",lambda a:next(r for r in a[2] if r["ModuleId"]=="MOD-13").__setitem__("Decision","W083_VERIFIED")),
    ]
    for name,alter in tests:
        a=copy.deepcopy(arrays);alter(a)
        try:verify(*a)
        except AssertionError:print("MUTATION_REJECTED",name)
        else:raise AssertionError("mutation falsely passed: "+name)
    return len(tests)

def main():
    p=argparse.ArgumentParser();p.add_argument("--repo-root",default=".");p.add_argument("--negative",action="store_true")
    opts=p.parse_args();root=Path(opts.repo_root)
    paths=[root/"docs/plan/v1.1-original/MODULE_21_OWNERS.csv",
           root/"reference/spec_v2_machine_readable/module_registry.csv",
           root/"docs/reviews/W003_MODULE_21_OWNER_REVIEW.csv",
           root/"docs/reviews/W003_MODULE_DAG_CANDIDATE.csv"]
    data=[read_csv(path) for path in paths]
    r=verify(*data)
    r["mutation_negative_rejections"]=mutation_negatives(data) if opts.negative else None
    r["source_sha256"]={str(path.relative_to(root)):hashlib.sha256(path.read_bytes()).hexdigest() for path in paths}
    r["status"]="STATIC_PREP_PASS_ROUTING_GATE_AND_A10_PENDING"
    print(json.dumps(r,ensure_ascii=False,indent=2,sort_keys=True))
    return 0
if __name__=="__main__":raise SystemExit(main())