#!/usr/bin/env python3
"""Source-lock and topology counts; does NOT certify functional implementation."""
from pathlib import Path
import csv, json, sys, hashlib
root=Path(sys.argv[1]).resolve()
def rows(name):
 with (root/'reference/spec_v2_machine_readable'/name).open(encoding='utf-8-sig',newline='') as f:return list(csv.DictReader(f))
def check(ok, note):
 if not ok:raise AssertionError(note)
lock=json.loads((root/'BASELINE_LOCK.json').read_text(encoding='utf-8'))
for name, h in lock['reference_files'].items():
 data=(root/'reference/spec_v2_machine_readable'/name).read_bytes()
 check(hashlib.sha256(data).hexdigest()==h,'spec snapshot modified '+name)
req,tc,mod,plc,gates,channels=[rows(f) for f in ('requirements_trace.csv','acceptance_cases.csv','module_registry.csv','plc_signal_registry.csv','field_verification_gates.csv','station_channel_map.csv')]
check(len(req)==114 and len(tc)==114 and len(mod)==21,'source count RQ/TC/MOD')
check(len(plc)==45 and len(gates)==14 and len(channels)==11,'source count PLC/FG/Channel')
check(len(set(x['RequirementId'] for x in req))==114,'duplicate requirements')
check(len(set(x['TestCaseId'] for x in tc))==114,'duplicate test cases')
check(len(set(x['ChannelId'] for x in channels))==11,'duplicate channels')
check(len(set(x['PhysicalDeviceId'] for x in channels))==9,'physical device count')
check(all(x['ProductionWriteAllowed'].strip().lower() in ('false','0','no') for x in gates),'site gate unexpectedly writable')
check(all(x['ProductionWriteEnabled'].strip().lower() in ('false','0','no') for x in plc),'unsafe PLC production writable')
check(sum(x['Status']=='BLOCKED' for x in gates)==7 and sum(x['Status']=='OPEN' for x in gates)==7,'field gate status')
check(sum(x['ChannelId'] in ('CH08','CH09') for x in channels)==2,'linescan back slots')
check(sum(x['ChannelId'] in ('CH10','CH11') for x in channels)==2,'linescan front slots')
with (root/'reference/plan_v1.1/WBS_TASKS_130.csv').open(encoding='utf-8-sig') as f: tasks=list(csv.DictReader(f))
check(len(tasks)==130 and len(set(x['TaskId'] for x in tasks))==130,'WBS task count or uniqueness')
check(all(x['Status']=='NOT_STARTED' for x in tasks),'DO NOT edit reference plan task status')
print('PASS: spec locks, 114 RQ / 114 TC / 21 MOD / 45 PLC / 14 FG / 9 devices / 11 channels / 130 WBS')
