#!/usr/bin/env python3
"""Owned-path source changes from one exact integrated source ZIP -> agent patch ZIP."""
import argparse,json
from pathlib import Path
from zip_common import all_tree_files, source_members, ownership, editable, sha, file_sha, archive_bytes, MAX_PATCH_BYTES, MAX_PATCH_FILES
p=argparse.ArgumentParser()
p.add_argument('--base',required=True);p.add_argument('--worktree',required=True)
p.add_argument('--agent',required=True,choices=['AOI-Agent01','AOI-Agent02','AOI-Agent03','AOI-Agent04','AOI-Agent05'])
p.add_argument('--tasks',required=True);p.add_argument('--out',required=True)
a=p.parse_args()
basepath=Path(a.base).resolve();work=Path(a.worktree).resolve()
_,old=source_members(basepath);new=all_tree_files(work)
# Ignore generated manifests in workspace; do not let agents overwrite source hash evidence.
allowed=ownership(old,a.agent)
changed=[];payload={};total=0
for name in sorted(set(old)|set(new)):
    if name in ('FILE_HASHES.json','SHA256SUMS.txt'):continue
    prev=old.get(name);next_=new.get(name)
    if prev==next_:continue
    if not editable(name,allowed):raise ValueError(f'{a.agent} cannot modify non-owned file: {name}')
    if next_ is None:raise ValueError('M00 protocol requires add/modify only; deletion needs A0 change request: '+name)
    total+=len(next_)
    if total>MAX_PATCH_BYTES or len(changed)>=MAX_PATCH_FILES:raise ValueError('patch too large for code-only handoff')
    changed.append({'path':name,'previous_sha256':sha(prev) if prev is not None else None,'new_sha256':sha(next_),'size':len(next_)})
    payload['payload/'+name]=next_
if not changed:raise ValueError('no owned source edits were found')
tasks=[x.strip() for x in a.tasks.split(',') if x.strip()]
if not tasks or any(len(t)!=4 or t[0]!='W' or not t[1:].isdigit() for t in tasks):raise ValueError('invalid task IDs')
meta={'schema':'aoi.zip.patch.v1','agent':a.agent,'task_ids':tasks,'base_release':json.loads(old['RELEASE.json'])['release_id'],
      'base_archive_sha256':file_sha(basepath),'contract_version':json.loads(old['RELEASE.json'])['contract_version'],
      'changed':changed,'status':'SUBMITTED_NOT_REVIEWED'}
payload['patch.json']=(json.dumps(meta,sort_keys=True,ensure_ascii=False,indent=2)+'\n').encode()
archive_bytes('',payload,Path(a.out))
print('PASS patch-created',a.out,'base_sha',meta['base_archive_sha256'],'changed',len(changed))
