#!/usr/bin/env python3
"""Reproducible W002 source-equality and fail-closed gates (no live PLC)."""
import csv,sys,copy,json
from pathlib import Path
def read(path):
    with open(path,encoding="utf-8-sig",newline="") as f:return list(csv.DictReader(f))
def check(root,mutate=False):
    p=Path(root)
    a=read(p/"reference/spec_v2_machine_readable/plc_signal_registry.csv")
    b=read(p/"docs/reviews/W002_STATIC_FACTS_45.csv")
    c=read(p/"reference/spec_v2_machine_readable/field_verification_gates.csv")
    d=read(p/"docs/reviews/W002_FIELD_GATE_OPEN_14.csv")
    e=read(p/"docs/plan/v1.1-original/FIELD_GATES_14.csv")
    def verify(b,d):
        assert len(a)==len(b)==45 and len(c)==len(d)==len(e)==14
        sa={r["SignalId"]:r for r in a};sb={r["SignalId"]:r for r in b}
        gc={r["GateId"]:r for r in c};gd={r["GateId"]:r for r in d};ge={r["GateId"]:r for r in e}
        assert len(sa)==len(sb)==45 and len(gc)==len(gd)==len(ge)==14
        assert sa.keys()==sb.keys() and gc.keys()==gd.keys()==ge.keys()
        for k,row in sb.items():
            assert all(sa[k][col]==value for col,value in row.items()),k
            assert row["ProductionWriteEnabled"]=="FALSE"
        assert sum(row["Status"]=="BLOCKED" for row in d)==7
        assert sum(row["Status"]=="OPEN" for row in d)==7
        for k,row in gd.items():
            assert all(gc[k][col]==value for col,value in row.items()),k
            assert row["ProductionWriteAllowed"]=="FALSE" and row["FieldSignoff"].strip()==""
            assert ge[k]["ProductionWriteAllowed"]=="FALSE"
        for address in ("D1030","D1000","D1013","D2000","D1016","D2099"):
            assert any(row["PLCAddress"].startswith(address) for row in b),address
        assert sum(row["PLCAddress"]=="D1013" for row in b)>=2
        assert sum(row["PLCAddress"]=="D1000" for row in b)>=2
    verify(b,d)
    rejects=0
    if mutate:
        for which,col,val in [(0,"PLCAddress","D9999"),(0,"ProductionWriteEnabled","TRUE"),(1,"ProductionWriteAllowed","TRUE"),(1,"FieldSignoff","FAKE"),(1,"Status","OPEN"),(0,"Direction","FAKE")]:
            bb,dd=copy.deepcopy(b),copy.deepcopy(d)
            (bb if which==0 else dd)[0][col]=val
            try:verify(bb,dd)
            except AssertionError:rejects+=1
            else:raise AssertionError("mutant falsely passed: "+col)
        assert rejects==6
    print(json.dumps({"signals":len(b),"field_gates":len(d),"blocked":7,"open":7,"mutations_rejected":rejects,"production_write":False,"scope":"STATIC_ONLY_NOT_FIELD_ACCEPTED"},sort_keys=True))
if __name__=="__main__":check(sys.argv[1] if len(sys.argv)>1 else ".", "--negative" in sys.argv)
