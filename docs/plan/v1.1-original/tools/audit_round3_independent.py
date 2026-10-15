#!/usr/bin/env python3
"""Independent ROUND3 plan audit focused on runnable execution topology.
Validates plan text together with task dependency closure, stage due dates, generator drift,
resource integrity and agent source ownership. Does not execute future AOI software.
"""
from pathlib import Path
import argparse,csv,json,hashlib,collections,re,subprocess,sys,os,tempfile,shutil
P=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser()
parser.add_argument('--spec-root',type=Path,required=True,help='Expanded v2.0 source dir')
parser.add_argument('--reference-dir',type=Path,required=True,help='Directory containing 3 raw reference inputs')
parser.add_argument('--output',type=Path,default=P/'REVIEW/audit_round3_independent.json')
a=parser.parse_args()
checks=[]
def check(label,ok,desc=''):
 checks.append({'check':label,'pass':bool(ok),'detail':desc})
 if not ok:print('FAIL',label,desc)
def readcsv(path):
 with path.open(encoding='utf-8-sig',newline='') as f:return list(csv.DictReader(f))
def digest(p):
 d=hashlib.sha256()
 with p.open('rb') as f:
  for block in iter(lambda:f.read(1<<20),b''):d.update(block)
 return d.hexdigest()
W=readcsv(P/'WBS_TASKS_130.csv'); R=readcsv(P/'RQ_114_COVERAGE.csv');S=readcsv(P/'SCENARIO_MATRIX.csv'); F=readcsv(P/'FIELD_GATES_14.csv'); U=readcsv(P/'UI_STAGE_INTEGRATION_MATRIX.csv'); E=readcsv(P/'EXIT_GATES.csv')
T={r['TaskId']:r for r in W};owner=json.loads((P/'AGENT_OWNERS.json').read_text());plan=(P/'PLAN.md').read_text(encoding='utf-8');lock=json.loads((P/'BASELINE_LOCK.json').read_text(encoding='utf-8'))
D={k:set(filter(None,r['DependsOn'].split(','))) for k,r in T.items()}
rank={f'S{i:02}':i for i in range(10)}|{'HARDWARE':10}
check('130 task IDs unique',len(W)==len(T)==130)
check('114 source requirement IDs unique',len(R)==len({x['RequirementId'] for x in R})==114)
check('52 independent simulation scenario IDs unique',len(S)==len({x['ScenarioId'] for x in S})==52)
check('10 UI staged integration proofs defined',len(U)==len({x['UIId'] for x in U})==10)
check('All 130 tasks not yet executed',all(x['Status']=='NOT_STARTED' for x in W))
check('All 114 requirements still not tested',all(x['CurrentExecutionState']=='NOT_RUN' for x in R))
check('All 52 simulations remain NOT_RUN',all(x['ExecutionStatus']=='NOT_RUN' for x in S))
check('All 14 production-write gates remain closed',len(F)==14 and all(x['ProductionWriteAllowed']=='FALSE' and x['FieldStatus']=='OPEN_NEEDS_SITE_EVIDENCE' for x in F))
check('Source/protocol RQ priority preserved',collections.Counter(x['Priority'] for x in R)=={'P0':78,'P1':36})
check('W049 E2E directly requires real source processing, results, parent image, DB and atomic outbox',{'W051','W058','W059','W060','W061','W062','W063','W045','W048','W055'}.issubset(D['W049']))
check('W033 5-process launcher requires working IPC, DB and safety guard',{'W016','W018','W021'}.issubset(D['W033']))
check('W045 peer business results require durable inbox/outbox protocol',{'W043'}.issubset(D['W045']))
check('W067 result preview is derived from rendered result, not raw SDK frame',{'W058','W063'}.issubset(D['W067']) and '结果图' in T['W067']['Acceptance'])
check('W058 explicitly renders structured overlay and actual result image',all(x in T['W058']['Deliverable']+T['W058']['Acceptance'] for x in ['结果图','overlay','hash']))
check('W016 mutating IPC deny by default before RBAC exists',all(x in T['W016']['Acceptance']+T['W016']['Deliverable'] for x in ['命令','拒绝','Core']))
check('W089 enforces session credentials and Core-side command auth',all(x in T['W089']['Acceptance'] for x in ['IPC','拒绝','授权']))
check('W086 cannot claim final prepared before PLC readback',all(x in T['W086']['Acceptance'] for x in ['绝不可报告PREPARED_BOTH','vPLC']) and 'PREPARED_BOTH' not in T['W086']['Deliverable'])
check('W087 commit waits for virtual PLC full parameter readback', 'W088' in D['W087'] and 'PREPARED_BOTH' in T['W087']['Deliverable'])
check('W094 QA-tested UI saving wired to product draft and auth',{'W084','W076','W077','W089','W090'}.issubset(D['W094']) and 'PatchStationDraft' in T['W094']['Deliverable'])
check('S05 early alarm UI cannot be falsely marked completed diagnostic', '骨架' in T['W078']['Title'] and 'G06' in T['W078']['Acceptance'])
check('S05 early stats UI explicitly requires later G07 verification', 'W100' in T['W079']['Acceptance'] and 'G07' in T['W079']['Acceptance'])
check('Qt Quick actual Windows plugin dependency explicit',all('Qt' in T[x]['Deliverable'] and any(k in T[x]['Deliverable']+T[x]['Acceptance'] for k in ['QML','Quick']) for x in ['W007','W011','W117']))
check('Do not treat Qt Widgets reference template as Qt Quick proof','Widgets' in plan and '不能' in plan)
check('Core binary ownership assigned A1', 'apps/aoi-core/**' in owner['A1']['paths'] and 'apps/aoi-core/**' not in owner['A0']['paths'])
check('Root CMake/CI belongs to A10',all(x in owner['A10']['paths'] for x in ['CMakeLists.txt','CMakePresets.json','scenarios/**']))
check('Agent prompts have same source paths as Owner manifest',all(f'`{v["paths"]}`' in (P/'AGENT_PROMPTS'/f'{k}_{"lead" if k=="A0" else "worker"}.md').read_text(encoding='utf-8') for k,v in owner.items()))
check('UI integration features point to real tasks',all(all(t in T for t in x['RequiredTasks'].split(',')) for x in U))
check('UI final phase not earlier than any required implementation task',all(rank[x['FinalRequiredPhase']]>=max(rank[T[t]['Phase']] for t in x['RequiredTasks'].split(',')) for x in U))
check('UI final Gate consistent with final phase',all('G'+x['FinalRequiredPhase'][1:]==x['FinalGate'] for x in U))
check('UI S05 early page gates respect backend dependency',all(next(x for x in U if x['UIId']==u)['FinalGate']==g for u,g in [('UI05','G06'),('UI06','G07'),('UI07','G07')]))
check('Scenario due phase not earlier than every referenced task',all(rank[x['DuePhase']]>=max(rank[T[t]['Phase']] for t in x['RelatedTaskIds'].split(',')) for x in S))
check('Every scenario evidence and machine assertion are populated',all(x['MandatoryEvidence'] and x['ExpectedMachineAssertion'] and x['FaultOrTrigger'] for x in S))
check('52 scenarios include new regression branch groups',all(c in {x['ScenarioId'] for x in S} for c in [f'SC-{i:03}' for i in range(45,53)]))
check('No task depends on itself, and all dependencies exist',all(k not in d and d<=T.keys() for k,d in D.items()))
seen={};visiting=set()
def dep(k):
 if k in visiting:raise RuntimeError('cycle '+k)
 if k in seen:return seen[k]
 visiting.add(k); val=1+max((dep(v) for v in D[k]),default=0);visiting.remove(k);seen[k]=val;return val
try:
 for k in T:dep(k)
 dag_ok=True
except Exception as exc:dag_ok=False;check('Dependency exception',False,str(exc))
check('WBS DAG is acyclic and still 37 waves',dag_ok and len(seen)==130 and max(seen.values())==37)
check('Only W001 initially READY', {k for k,d in D.items() if not d}=={'W001'})
check('No task depends on future development phase',all(rank[T[t]['Phase']]<=rank[x['Phase']] for x in W for t in D[x['TaskId']]))
check('Every RQ SupportingWBS exists and never repeats PrimaryWBS',all(r['PrimaryWBS'] not in r['SupportingWBS'].split(',') and all(t in T for t in filter(None,r['SupportingWBS'].split(','))) for r in R))
check('Original spec root readable',a.spec_root.is_dir())
if a.spec_root.is_dir():
 v2=readcsv(a.spec_root/'07_可机读清单/requirements_trace.csv')
 tc=readcsv(a.spec_root/'07_可机读清单/acceptance_cases.csv')
 fg=readcsv(a.spec_root/'07_可机读清单/field_verification_gates.csv')
 check('All 114 requirement text, spec file, TC pointer equals real v2 source',len(v2)==len(R) and all(x['RequirementId']==y['RequirementId'] and x['RequirementCN']==y['Requirement'] and x['SpecFile']==y['SpecFile'] and x['TestCaseIds']==y['TestCaseIds'] for x,y in zip(v2,R)))
 check('All original 114 case IDs and RQ links are the same',len(tc)==114 and all(r['TestCaseIds']==t['TestCaseId'] and t['RequirementId']==r['RequirementId'] for r,t in zip(R,tc)))
 check('All 14 Field Gate original statuses preserved',len(fg)==14 and all(x['GateId']==y['GateId'] and x['Status']==y['OriginalStatus'] for x,y in zip(fg,F)))
for typ,meta in lock['source_files'].items():
 f=a.reference_dir/meta['filename']
 check('Actual SHA256 of '+typ, f.is_file() and digest(f)==meta['sha256'] and f.stat().st_size==meta['size_bytes'],str(f))
# This gate checks reproducible derivative tables: does not execute AOI Core.
r=subprocess.run([sys.executable,str(P/'tools/rebuild_wbs_derived.py'),'--check'],capture_output=True,text=True)
check('DAG waves and per-agent task cards match WBS exactly',r.returncode==0,r.stdout+r.stderr)
with tempfile.TemporaryDirectory(prefix='aoi_plan_gen_') as q:
 tmp=Path(q);(tmp/'tools').mkdir();shutil.copyfile(P/'tools/generate_matrices.py',tmp/'tools/generate_matrices.py')
 rr=subprocess.run([sys.executable,str(tmp/'tools/generate_matrices.py')],capture_output=True,text=True)
 check('Scenario/exit/risk generator successfully recreates csv',rr.returncode==0,rr.stdout+rr.stderr)
 if rr.returncode==0:
  for f in ['SCENARIO_MATRIX.csv','EXIT_GATES.csv','RISK_REGISTER.csv']:
   check(f+' byte-identical to current generator output',(tmp/f).read_bytes()==(P/f).read_bytes())
check('No scenario/RQ or field tests accidentally marked PASS',all(x['Status']=='NOT_RUN' for x in E if x['Gate']!='FIELD'))
check('Versioned user handoff and revised constraints both present', 'v1.1' in plan and 'AGENT_BOOTSTRAP_DEPS.md' in [x.name for x in P.iterdir()] and 'UI_STAGE_INTEGRATION_MATRIX.csv' in plan)
check('Simulation requires clear separation of synthetic identity',all(x in (P/'SIMULATION_CONTRACT.md').read_text(encoding='utf-8') for x in ['SimSlotOccupancy','SimStationTriggerSequence','SIM_ONLY']))
report={'audit':'REVIEW_ROUND3_INDEPENDENT','scope':'PLAN_ONLY_REAL_SOURCE_CROSSCHECK_NO_AOI_IMPLEMENTATION_TESTED','checks':checks,'check_count':len(checks),'pass_count':sum(x['pass'] for x in checks),'fail_count':sum(not x['pass'] for x in checks),'original_source_root':str(a.spec_root),'source_fingerprint_root':str(a.reference_dir)}
a.output.parent.mkdir(parents=True,exist_ok=True)
a.output.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print('ROUND3_RESULT',json.dumps({k:report[k] for k in ['check_count','pass_count','fail_count']},ensure_ascii=False))
sys.exit(1 if report['fail_count'] else 0)
