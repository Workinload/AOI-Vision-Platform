#!/usr/bin/env python3
"""Create a new non-authoritative CANDIDATE ZIP; A10/A0 must separately certify it."""
import argparse,json,re,csv,io
from pathlib import Path
from zip_common import manifests, archive_bytes,source_members,file_sha
from validate_patch import validate
p=argparse.ArgumentParser()
p.add_argument('--base',required=True);p.add_argument('--patch',required=True);p.add_argument('--out',required=True);p.add_argument('--release-id',required=True)
a=p.parse_args()
if not re.fullmatch(r'M\d{2}-r\d{3}',a.release_id):raise ValueError('release ID must look like M00-r002')
base=Path(a.base).resolve(); patch=Path(a.patch).resolve();out=Path(a.out).resolve()
if out.exists():raise ValueError('do not overwrite an existing official or candidate source ZIP')
meta,entries,current=validate(base,patch)
plan_rows={r['TaskId']:r for r in csv.DictReader(io.StringIO(current['reference/plan_v1.1/WBS_TASKS_130.csv'].decode('utf-8-sig')))}
ledger=json.loads(current['docs/agents/TASK_LEDGER.json'])['items']
for taskid in meta['task_ids']:
    row=plan_rows[taskid]
    prereqs=[x.strip() for x in row['DependsOn'].split(',') if x.strip()]
    incomplete=[x for x in prereqs if ledger.get(x,{}).get('status')!='VERIFIED']
    if incomplete:
        raise ValueError(f'{taskid} cannot be integrated; prerequisite tasks not VERIFIED: {incomplete}')
if a.release_id==meta['base_release']:raise ValueError('must use a new release ID')
for item in meta['changed']:
    current[item['path']]=entries['payload/'+item['path']]
release=json.loads(current['RELEASE.json'])
release['release_id']=a.release_id
release['status']='CANDIDATE_UNVERIFIED'
release['parent_release']=meta['base_release']
release['parent_archive_sha256']=meta['base_archive_sha256']
release['last_patch_agent']=meta['agent']
release['last_patch_tasks']=meta['task_ids']
current['RELEASE.json']=(json.dumps(release,ensure_ascii=False,sort_keys=True,indent=2)+'\n').encode()
current=manifests(current)
archive_bytes('AOI-Vision-Platform-'+a.release_id+'/',current,out)
source_members(out)
print('PASS CANDIDATE CREATED (NOT QA-APPROVED)',out,'sha256',file_sha(out))
