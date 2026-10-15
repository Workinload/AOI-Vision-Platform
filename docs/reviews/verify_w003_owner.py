#!/usr/bin/env python3
import csv,sys,copy
from pathlib import Path
r=Path(sys.argv[1] if len(sys.argv)>1 else ".")
def read(p):
 with open(r/p,encoding="utf-8-sig",newline="") as f:return list(csv.DictReader(f))
p=read("docs/plan/v1.1-original/MODULE_21_OWNERS.csv");v=read("reference/spec_v2_machine_readable/module_registry.csv");m=read("docs/reviews/W003_MODULE_21_OWNER_REVIEW.csv")
assert len(p)==len(v)==len(m)==21
pm={x["ModuleId"]:x for x in p};vm={x["ModuleId"]:x for x in v};mm={x["ModuleId"]:x for x in m};assert len(pm)==len(vm)==len(mm)==21 and pm.keys()==vm.keys()==mm.keys()
roles={"A1":"AOI-Agent01","A2":"AOI-Agent02","A3":"AOI-Agent02","A4":"AOI-Agent03","A5":"AOI-Agent01","A6":"AOI-Agent03","A7":"AOI-Agent06","A8":"AOI-Agent04","A9":"AOI-Agent04","A10":"AOI-Agent05"}
for key,original in pm.items():
 assert all(mm[key][k]==val for k,val in original.items())
 assert vm[key]["ModuleName"]==original["ModuleName"]
 assert mm[key]["WorkerChat"]==roles[original["DevOwner"]]
assert len([x for x in m if x["DevOwner"]=="A7"])==2
print("W003_STATIC_MAPPING_PASS 21/21 A7=2; routing PR12 A10 PASS and merged")