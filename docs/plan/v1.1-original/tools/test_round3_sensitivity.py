#!/usr/bin/env python3
"""Prove the independent plan checker rejects deliberately defective plan copies.
Copies plans to a temporary dir and only mutates those temporary copies.
"""
from pathlib import Path
import argparse, csv, tempfile, shutil, subprocess, sys, json
SRC=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser()
parser.add_argument('--spec-root',type=Path,required=True)
parser.add_argument('--reference-dir',type=Path,required=True)
parser.add_argument('--output',type=Path,default=SRC/'REVIEW/audit_round3_sensitivity.json')
a=parser.parse_args()

def edit_csv(path,pred,changes):
 with path.open('r',encoding='utf-8-sig',newline='') as f:
  r=csv.DictReader(f); cols=r.fieldnames; rows=list(r)
 matched=[row for row in rows if pred(row)]
 if len(matched)!=1:raise RuntimeError('expected single mutation target '+str(path))
 for k,v in changes.items(): matched[0][k]=v(matched[0][k]) if callable(v) else v
 with path.open('w',encoding='utf-8-sig',newline='') as f:
  wr=csv.DictWriter(f,fieldnames=cols);wr.writeheader();wr.writerows(rows)

cases=[
 ('remove_e2e_durability_dependency',lambda p:edit_csv(p/'WBS_TASKS_130.csv',lambda r:r['TaskId']=='W049',{'DependsOn':lambda v:','.join(x for x in v.split(',') if x!='W062')}),'W049 E2E directly requires'),
 ('schedule_scenario_before_prereq',lambda p:edit_csv(p/'SCENARIO_MATRIX.csv',lambda r:r['ScenarioId']=='SC-034',{'DuePhase':'S03'}),'Scenario due phase'),
 ('incorrectly_allow_live_plc_write',lambda p:edit_csv(p/'FIELD_GATES_14.csv',lambda r:r['GateId']=='FG-01',{'ProductionWriteAllowed':'TRUE'}),'All 14 production-write gates'),
 ('premature_prepared_both',lambda p:edit_csv(p/'WBS_TASKS_130.csv',lambda r:r['TaskId']=='W086',{'Acceptance': '已STAGED即视为PREPARED_BOTH可开新料'}),'W086 cannot claim final prepared'),
 ('remove_core_owner',lambda p:(p/'AGENT_OWNERS.json').write_text((p/'AGENT_OWNERS.json').read_text().replace(',apps/aoi-core/**',''),encoding='utf-8'),'Core binary ownership assigned'),
 ('corrupt_sim_test_result',lambda p:edit_csv(p/'RQ_114_COVERAGE.csv',lambda r:r['RequirementId']=='RQ-001',{'CurrentExecutionState':'SIM_PASS'}),'All 114 requirements still not tested'),
]
result=[]
for name,edit,needle in cases:
 with tempfile.TemporaryDirectory(prefix='aoi_sensitivity_') as td:
  q=Path(td)/'plan'; shutil.copytree(SRC,q)
  edit(q)
  rep=q/'REVIEW/result_sensitivity.json'
  args=[sys.executable,str(q/'tools/audit_round3_independent.py'),'--spec-root',str(a.spec_root),'--reference-dir',str(a.reference_dir),'--output',str(rep)]
  r=subprocess.run(args,capture_output=True,text=True)
  data=json.loads(rep.read_text()) if rep.exists() else {}
  got=[x['check'] for x in data.get('checks',[]) if not x.get('pass')]
  recognized= r.returncode!=0 and any(needle in x for x in got)
  result.append({'mutation':name,'detected':recognized,'exit_code':r.returncode,'failed_checks':got})
  print(('PASS sensitivity' if recognized else 'FAIL sensitivity'),name,'EXIT',r.returncode)
summary={'kind':'PLAN_AUDIT_MUTATION_TEST_NO_REAL_AOI_PROGRAMS','mutations':len(result),'caught':sum(x['detected'] for x in result),'missed':sum(not x['detected'] for x in result),'cases':result}
a.output.parent.mkdir(parents=True,exist_ok=True)
a.output.write_text(json.dumps(summary,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print('MUTATION_RESULT',json.dumps({k:summary[k] for k in ('mutations','caught','missed')}))
sys.exit(1 if summary['missed'] else 0)
