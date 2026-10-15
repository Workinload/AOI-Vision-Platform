#!/usr/bin/env python3
"""Pure read-only patch verification. Refuse wrong baseline and forbidden paths."""
import argparse,json,zipfile,csv,io
from pathlib import Path
from zip_common import source_members,ownership,editable,sha,file_sha,verify_zip_members,safe_rel,MAX_PATCH_BYTES,MAX_PATCH_FILES

def validate(base:Path,patch:Path):
    _,current=source_members(base)
    with zipfile.ZipFile(patch) as z:
        verify_zip_members(z)
        entries={i.filename:z.read(i) for i in z.infolist() if not i.is_dir()}
    if 'patch.json' not in entries:raise ValueError('missing patch.json')
    meta=json.loads(entries['patch.json'])
    if meta.get('schema')!='aoi.zip.patch.v1':raise ValueError('patch schema mismatch')
    if meta.get('base_archive_sha256')!=file_sha(base):raise ValueError('wrong exact baseline ZIP SHA256; REBASE REQUIRED')
    rel=json.loads(current['RELEASE.json'])
    if meta.get('base_release')!=rel['release_id']:raise ValueError('wrong release id')
    if meta.get('contract_version')!=rel['contract_version']:raise ValueError('contract mismatch; CR required')
    if meta.get('status')!='SUBMITTED_NOT_REVIEWED':raise ValueError('patch cannot self-approve')
    allowed=ownership(current,meta.get('agent',''))
    reg=json.loads(current['docs/agents/OWNERSHIP.json'])
    authorized_roles=set(reg['chats'][meta['agent']])
    plan_rows={r['TaskId']:r for r in csv.DictReader(io.StringIO(current['reference/plan_v1.1/WBS_TASKS_130.csv'].decode('utf-8-sig')))}
    tasks=meta.get('task_ids',[])
    if not tasks or len(tasks)!=len(set(tasks)) or any(len(t)!=4 or t[0]!='W' or not t[1:].isdigit() for t in tasks):raise ValueError('task IDs invalid')
    for task in tasks:
        if task not in plan_rows or plan_rows[task]['Owner'] not in authorized_roles:
            raise ValueError(f'agent {meta["agent"]} does not own WBS task {task}')
    changed=meta.get('changed',[])
    if not isinstance(changed,list) or not changed or len(changed)>MAX_PATCH_FILES:raise ValueError('invalid patch file list')
    used=set();payload_bytes=0
    for item in changed:
        name=safe_rel(item['path'])
        if name in used:raise ValueError('duplicate changed path')
        used.add(name)
        if name in ('FILE_HASHES.json','SHA256SUMS.txt'):raise ValueError('cannot forge source manifest')
        if not editable(name,allowed):raise ValueError('unowned source path '+name)
        orig=current.get(name)
        if (sha(orig) if orig is not None else None)!=item.get('previous_sha256'):
            raise ValueError('previous file hash differs: '+name)
        packed='payload/'+name
        if packed not in entries:raise ValueError('payload missing '+packed)
        body=entries[packed]
        if sha(body)!=item['new_sha256'] or len(body)!=item['size']:raise ValueError('payload sha/size mismatch '+name)
        payload_bytes+=len(body)
        if payload_bytes>MAX_PATCH_BYTES:raise ValueError('large payload/SDK in patch')
    if set(entries)!= {'patch.json'} | {'payload/'+n for n in used}:
        raise ValueError('extra or hidden ZIP payload members')
    return meta,entries,current

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--base',required=True);p.add_argument('--patch',required=True)
    a=p.parse_args()
    m,_,_=validate(Path(a.base),Path(a.patch))
    print('PASS VERIFIED_PATCH',m['agent'],m['base_release'],len(m['changed']),'files',','.join(m['task_ids']))
