#!/usr/bin/env python3
"""Rebuild dependency waves and per-agent task cards from WBS. No AOI software is executed.
Use --check in CI to verify derivative artifacts; default refreshes them.
"""
from pathlib import Path
import argparse, csv, io, collections, sys
P=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser()
parser.add_argument('--check',action='store_true')
a=parser.parse_args()
with (P/'WBS_TASKS_130.csv').open(encoding='utf-8-sig',newline='') as f:W=list(csv.DictReader(f))
with (P/'RQ_114_COVERAGE.csv').open(encoding='utf-8-sig',newline='') as f:R=list(csv.DictReader(f))
T={r['TaskId']:r for r in W}
if len(T)!=len(W):raise ValueError('duplicate task id')
D={k:set(filter(None,v['DependsOn'].split(','))) for k,v in T.items()}
depth={};busy=set()
def calc(k):
 if k in depth:return depth[k]
 if k in busy:raise ValueError('CYCLE: '+k)
 busy.add(k)
 if not D[k]<=T.keys():raise ValueError('Unknown depends: '+k)
 depth[k]=1+max((calc(t) for t in D[k]),default=0)
 busy.remove(k)
 return depth[k]
for k in T:calc(k)
wave_count=max(depth.values())
if wave_count!=37:raise ValueError(f'waves changed from 37 to {wave_count}: revise references and filename before merge')
changes=[]
def update(path,content):
 if a.check:
  if not path.is_file() or path.read_bytes()!=content:changes.append(str(path.relative_to(P)))
 else:
  path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(content)
# wave table
stream=io.StringIO(newline='');wr=csv.writer(stream)
wr.writerow(['Wave','TaskId','Phase','Owner','DependsOn','TaskTitle','InitialReadiness','CurrentState'])
for tid in sorted(T,key=lambda x:(depth[x],x)):
 r=T[tid]
 wr.writerow([f'{depth[tid]:02}',tid,r['Phase'],r['Owner'],r['DependsOn'],r['Title'],'READY_AT_START' if depth[tid]==1 else 'READY_ONLY_AFTER_DEPENDENCIES',r['Status']])
update(P/'DEPENDENCY_WAVES_37.csv',('\ufeff'+stream.getvalue()).encode('utf-8'))
# reverse trace of primary and supporting owners
rq_by_task=collections.defaultdict(set)
for r in R:
 rq_by_task[r['PrimaryWBS']].add(r['RequirementId']+'(P)')
 for t in filter(None,r['SupportingWBS'].split(',')):
  rq_by_task[t].add(r['RequirementId']+'(S)')
for aid in [f'A{i}' for i in range(11)]:
 rows=sorted((x for x in W if x['Owner']==aid),key=lambda x:(depth[x['TaskId']],x['TaskId']))
 b=[f'# {aid} · 独立 Agent 开发任务卡（从WBS自动生成）','',f'**任务数**：{len(rows)} ｜ **计划执行依赖波次**：1～{wave_count} ｜ **状态**：开发未开始。', '', '请先检查每个任务的 `DependsOn`；未满足依赖不得开工。每个任务均需附真实命令、测试输入、退出码、Trace/哈希、审阅人。','']
 for r in rows:
  t=r['TaskId'];pr='，'.join(sorted(rq_by_task[t])) or '该任务为工程/集成/门禁的间接依赖'
  b.extend([f'## {t} | {r["Title"]}（{r["Phase"]} / 波次 {depth[t]}）',
   f'- **前置任务**：{r["DependsOn"] or "无"}；**优先级**：{r["Priority"]}；**独立审阅人**：{r["Reviewer"]}。',
   f'- **具体产物**：{r["Deliverable"]}。',
   f'- **必须判定的验收条件**：{r["Acceptance"]}。',
   f'- **原 v2.0 设计章节引用**：`{r["SpecRefs"]}`。',
   f'- **覆盖的 RQ**：{pr}。',
   f'- **必须保存的证据**：{r["ExitEvidence"]}；当前状态 `{r["Status"]}`。', ''])
 update(P/'AGENT_TASK_CARDS'/f'{aid}_TASKS.md', ('\n'.join(b).rstrip()+'\n').encode('utf-8'))
if changes:
 print('DERIVED CONTENT DRIFT:',', '.join(changes))
 sys.exit(1)
print('OK tasks',len(W),'waves',wave_count,'agent cards',11, 'mode', 'check' if a.check else 'write')
