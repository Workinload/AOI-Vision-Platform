#!/usr/bin/env python3
"""PLAN review 1: independent structural/source trace audit. Does not test AOI software."""
import csv,json,re,hashlib,sys,collections
from pathlib import Path
p=Path(__file__).resolve().parents[1]
def readcsv(filename):
 with (p/filename).open(encoding='utf-8-sig',newline='') as f:return list(csv.DictReader(f))
checks=[]
def chk(name,cond,detail=''):
 checks.append({'check':name,'pass':bool(cond),'detail':detail})
 if not cond:print('FAIL',name,detail)

tasks=readcsv('WBS_TASKS_130.csv'); rq=readcsv('RQ_114_COVERAGE.csv');modules=readcsv('MODULE_21_OWNERS.csv');gates=readcsv('FIELD_GATES_14.csv'); cases=readcsv('SCENARIO_MATRIX.csv');exits=readcsv('EXIT_GATES.csv');risk=readcsv('RISK_REGISTER.csv')
team=json.loads((p/'AGENT_OWNERS.json').read_text(encoding='utf-8'))
T={r['TaskId']:r for r in tasks}; ids=set(T);owners=set(team)
chk('130 WBS unique and sequential',len(tasks)==len(T)==130 and ids=={f'W{i:03}' for i in range(1,131)},f'{len(tasks)} count')
chk('114 requirements unique and sequential',len(rq)==len({r['RequirementId'] for r in rq})==114 and {r['RequirementId'] for r in rq}=={f'RQ-{i:03}' for i in range(1,115)})
chk('21 modules unique',len(modules)==len({x['ModuleId'] for x in modules})==21)
chk('14 field gates unique',len(gates)==len({x['GateId'] for x in gates})==14)
chk('11 agent roles',owners=={f'A{i}' for i in range(11)})
chk('Every WBS has an assigned owner/reviewer and QA segregation',all(r['Owner'] in owners and r['Reviewer'] in owners and r['Owner']!=r['Reviewer'] for r in tasks))
chk('No missing WBS dependencies',all(d in ids and d!=r['TaskId'] for r in tasks for d in filter(None,r['DependsOn'].split(','))))
D={r['TaskId']:set(filter(None,r['DependsOn'].split(','))) for r in tasks}
resolved=set();waves=[]
while True:
 ready=sorted(k for k,v in D.items() if k not in resolved and v<=resolved)
 if not ready:break
 resolved.update(ready);waves.append(ready)
chk('WBS dependency DAG acyclic',len(resolved)==len(D),f'topological waves={len(waves)}, resolved={len(resolved)}')
depth={}
def calc_depth(x):
 if x not in depth:depth[x]=1+max([calc_depth(d) for d in D[x]]+[0])
 return depth[x]
for x in D:calc_depth(x)
wave_rows=readcsv('DEPENDENCY_WAVES_37.csv')
chk('Topological execution waves agree with WBS, and only W001 initially ready',len(wave_rows)==130 and len(waves)==37 and {k for k,v in D.items() if not v}=={'W001'} and all(int(x['Wave'])==depth[x['TaskId']] for x in wave_rows))
ph_order={f'S{i:02}':i for i in range(10)}|{'HARDWARE':10}
chk('No earlier phase blocked by a future phase',all(ph_order[T[d]['Phase']]<=ph_order[r['Phase']] for r in tasks for d in D[r['TaskId']]))
chk('Offline+hardware exact allocation',sum(r['Phase']!='HARDWARE' for r in tasks)==122 and sum(r['Phase']=='HARDWARE' for r in tasks)==8)
chk('No WBS already falsely marked completed',all(r['Status']=='NOT_STARTED' for r in tasks))
chk('Each 114 RQ primary WBS points to offline task',all(r['PrimaryWBS'] in T and T[r['PrimaryWBS']]['Phase']!='HARDWARE' for r in rq))
chk('All RQ supporting tasks exist and are unique per RQ',all(all(t in T for t in filter(None,r['SupportingWBS'].split(','))) and len(list(filter(None,r['SupportingWBS'].split(','))))==len(set(filter(None,r['SupportingWBS'].split(',')))) for r in rq))
chk('RQ Owner agrees with primary WBS',all(r['Owner']==T[r['PrimaryWBS']]['Owner'] for r in rq))
chk('114 RQ test case ID has same index',all(r['TestCaseIds']==r['RequirementId'].replace('RQ-','TC-') for r in rq))
chk('Baseline priority distribution=78 P0 +36 P1',collections.Counter(r['Priority'] for r in rq)=={'P0':78,'P1':36})
chk('All 114 tests currently NOT_RUN',all(r['CurrentExecutionState']=='NOT_RUN' for r in rq))
chk('Module Owners all valid',all(r['DevOwner'] in owners for r in modules))
chk('14 field gates all disallow production writes',all(r['ProductionWriteAllowed']=='FALSE' and r['FieldStatus']=='OPEN_NEEDS_SITE_EVIDENCE' for r in gates))
chk('7 BLOCKED+7 OPEN original gate labels kept',collections.Counter(r['OriginalStatus'] for r in gates)=={'BLOCKED':7,'OPEN':7})
chk('All 10 offline exit gates NOT_RUN and FIELD_PENDING',len(exits)==11 and all(r['Status']=='NOT_RUN' for r in exits if r['Gate']!='FIELD') and any(r['Gate']=='FIELD' and r['Status']=='FIELD_PENDING' for r in exits))
chk('Every test scenario has a valid unique ID and explicit expected assertion',len(cases)==len({r['ScenarioId'] for r in cases}) and len(cases)>=30 and all(r['ExpectedMachineAssertion'] and r['MandatoryEvidence'] for r in cases))
chk('Scenarios only refer to existing Task IDs',all(t in T for r in cases for t in r['RelatedTaskIds'].split(',')))
chk('Every test scenario NOT_RUN',all(r['ExecutionStatus']=='NOT_RUN' for r in cases))
chk('Risk registry is non-empty and assigned',len(risk)>=10 and all(r['Description'] and r['Owner'] for r in risk))
chk('Every Agent has independent prompt',all(any((p/'AGENT_PROMPTS'/f'A{i}_{kind}.md').exists() for kind in ('lead','worker')) for i in range(11)))
chk('Every Agent has a detailed execution task-card handoff',all((p/'AGENT_TASK_CARDS'/f'A{i}_TASKS.md').exists() and all(r['TaskId'] in (p/'AGENT_TASK_CARDS'/f'A{i}_TASKS.md').read_text(encoding='utf-8') for r in tasks if r['Owner']==f'A{i}') for i in range(11)))
chk('Main explanatory/supporting docs exist',all((p/f).exists() for f in ['PLAN.md','SIMULATION_CONTRACT.md','AGENT_STARTER.md','CHANGE_REQUEST_TEMPLATE.md','CONFIG/sim_profile.example.json']))
chk('Main PLAN has stages and gate sections',all(f'### S{i:02}' in (p/'PLAN.md').read_text(encoding='utf-8') for i in range(10)))
chk('SIM profile is parseable JSON and physical write disabled',not json.loads((p/'CONFIG/sim_profile.example.json').read_text(encoding='utf-8'))['io_policy']['production_plc_writes_enabled'])
chk('Mandatory packaging checkpoint precedes optional Wine QA', 'W120' in D['W118'] and 'W118' not in D['W120'])
chk('Deployment readiness only after simulated PLC readback', 'W088' in D['W087'])
chk('Full E2E only after localization, mock algorithm and image write',{'W048','W055','W059'}.issubset(D['W049']))

# Optional cross-check against original v2.0 extracted files; mandatory in this review session.
if len(sys.argv)==2:
 S=Path(sys.argv[1]);chk('Original source project directory present',S.exists())
 def srcread(f):
  with (S/f).open(encoding='utf-8-sig',newline='') as x:return list(csv.DictReader(x))
 if S.exists():
  original=srcread('07_可机读清单/requirements_trace.csv')
  testcases=srcread('07_可机读清单/acceptance_cases.csv')
  originalmods=srcread('07_可机读清单/module_registry.csv')
  originalfg=srcread('07_可机读清单/field_verification_gates.csv')
  chk('RQ text/spec/test pointers identical to v2.0 original',len(original)==len(rq) and all(a['RequirementId']==b['RequirementId'] and a['RequirementCN']==b['Requirement'] and a['SpecFile']==b['SpecFile'] and a['TestCaseIds']==b['TestCaseIds'] for a,b in zip(original,rq)))
  tcids={x['TestCaseId'] for x in testcases}
  chk('All copied testcase pointers exist in original acceptance cases',all(x['TestCaseIds'] in tcids for x in rq),f'original tests={len(tcids)}')
  chk('Module count and IDs match v2.0', {r['ModuleId'] for r in modules}=={r['ModuleId'] for r in originalmods})
  chk('Field gate count and IDs match v2.0', {r['GateId'] for r in gates}=={r['GateId'] for r in originalfg})
  chk('Each plan WBS section source ref resolves to source chapter',all(any(z for z in (S/{'00':'00_项目基线','01':'01_架构与协议','02':'02_设备与PLC','03':'03_数据与方案','04':'04_UI与交互','05':'05_安全运维','06':'06_实施与验收','07':'07_可机读清单'}[s.split(':')[0]]).glob(s.split(':')[1]+'_*')) if ':' in s and s.split(':')[1].isdigit() else True for r in tasks for s in r['SpecRefs'].split(';')))

report={'audit':'REVIEW_ROUND1_STRUCTURE','scope':'PLAN_ONLY_NO_AOI_SOFTWARE_EXECUTION','check_count':len(checks),'pass_count':sum(x['pass'] for x in checks),'fail_count':sum(not x['pass'] for x in checks),'checks':checks,'summary':{'wbs':len(tasks),'requirements':len(rq),'modules':len(modules),'field_gates':len(gates),'test_scenarios':len(cases),'topological_waves':len(waves)}}
(p/'REVIEW'/'audit_round1_structure.json').write_text(json.dumps(report,indent=2,ensure_ascii=False),encoding='utf-8')
print(json.dumps({k:report[k] for k in ['audit','check_count','pass_count','fail_count','summary']},ensure_ascii=False))
sys.exit(1 if report['fail_count'] else 0)
