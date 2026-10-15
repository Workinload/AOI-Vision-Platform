#!/usr/bin/env python3
"""Shared strict and deterministic-ish file+archive helpers (no Git required)."""
from __future__ import annotations
import hashlib
import json
from pathlib import Path, PurePosixPath
import stat
import zipfile
import os

FORBIDDEN_PARTS={'.git','.svn','__pycache__','.DS_Store'}
TOP_LEVEL_GENERATED={'build','runs','evidence'}
MAX_PATCH_BYTES=60_000_000
MAX_PATCH_FILES=1000

def sha(data:bytes)->str:return hashlib.sha256(data).hexdigest()
def file_sha(path:Path)->str:
    h=hashlib.sha256()
    with path.open('rb') as f:
        for piece in iter(lambda:f.read(1024*1024),b''):h.update(piece)
    return h.hexdigest()

def safe_rel(path:str)->str:
    if not path or '\\' in path or '\x00' in path or ':' in path or path.startswith('/'):
        raise ValueError('unsafe path '+repr(path))
    pp=PurePosixPath(path)
    if any(part in ('','.', '..') for part in path.split('/')) or not pp.parts:
        raise ValueError('traversal or invalid path '+repr(path))
    if any(part in FORBIDDEN_PARTS for part in pp.parts) or pp.parts[0] in TOP_LEVEL_GENERATED or pp.parts[0].startswith('build-'):
        raise ValueError('generated/forbidden path '+repr(path))
    if len(path)>230:raise ValueError('path too long')
    return pp.as_posix()

def verify_zip_members(z:zipfile.ZipFile):
    seen=set();total=0
    for i in z.infolist():
        name=i.filename
        if name.endswith('/'):continue
        if name in seen:raise ValueError('duplicate archive member '+name)
        seen.add(name)
        if name.startswith('/') or '\\' in name or '\x00' in name or any(p in ('', '.', '..') for p in name.split('/')):
            raise ValueError('unsafe archive path '+repr(name))
        typ=(i.external_attr >> 16)&0o170000
        if typ==stat.S_IFLNK:raise ValueError('symlink forbidden '+name)
        if i.file_size > MAX_PATCH_BYTES:raise ValueError('single file is too large '+name)
        total+=i.file_size
        if total>500_000_000:raise ValueError('archive expanded size too large')
    if z.testzip() is not None:raise ValueError('bad ZIP CRC')

def source_members(zip_path:Path)->tuple[str,dict[str,bytes]]:
    with zipfile.ZipFile(zip_path) as z:
        verify_zip_members(z)
        files=[i.filename for i in z.infolist() if not i.is_dir()]
        prefixes={name.split('/')[0] for name in files}
        if len(prefixes)!=1:raise ValueError('baseline ZIP requires exactly one top folder')
        prefix=prefixes.pop()+'/'
        content={safe_rel(name[len(prefix):]):z.read(name) for name in files}
    if 'FILE_HASHES.json' not in content or 'RELEASE.json' not in content:
        raise ValueError('baseline missing own hash/RELEASE manifest')
    expected=json.loads(content['FILE_HASHES.json'])['files']
    hashes={n:sha(b) for n,b in content.items() if n not in ('FILE_HASHES.json','SHA256SUMS.txt')}
    if hashes!=expected:raise ValueError('baseline content differs from its file manifest')
    if 'SHA256SUMS.txt' in content:
        lines=content['SHA256SUMS.txt'].decode().strip().splitlines()
        if len(lines)!=len(hashes)+1:raise ValueError('SHA256SUMS count')
        for line in lines:
            digest,rel=line.split('  ',1)
            actual=sha(content[rel])
            if digest!=actual:raise ValueError('SHA256SUMS mismatch '+rel)
    return prefix,content

def all_tree_files(root:Path)->dict[str,bytes]:
    files={}
    for path in sorted(root.rglob('*')):
        rel=path.relative_to(root).as_posix()
        relparts=PurePosixPath(rel).parts
        if any(part in FORBIDDEN_PARTS for part in relparts) or relparts[0] in TOP_LEVEL_GENERATED or relparts[0].startswith('build-'):continue
        if path.is_symlink():raise ValueError('Source tree symlink not allowed: '+str(path))
        if not path.is_file():continue
        safe_rel(rel)
        if rel in ('FILE_HASHES.json','SHA256SUMS.txt'):continue
        files[rel]=path.read_bytes()
    return files

def ownership(archive_files:dict[str,bytes], agent:str)->list[str]:
    reg=json.loads(archive_files['docs/agents/OWNERSHIP.json'])
    roles=reg['chats'].get(agent)
    if not roles:raise ValueError('unknown agent; valid '+str(sorted(reg['chats'])))
    return [p for role in roles for p in reg['roles'][role]]

def editable(rel:str,paths:list[str])->bool:
    return any((rel.startswith(p) if p.endswith('/') else rel==p) for p in paths)

def archive_bytes(prefix:str, filecontent:dict[str,bytes], out:Path):
    out.parent.mkdir(parents=True,exist_ok=True)
    with zipfile.ZipFile(out,'w',compression=zipfile.ZIP_DEFLATED,compresslevel=8,allowZip64=True) as z:
        for rel,data in sorted(filecontent.items()):
            safe_rel(rel)
            zi=zipfile.ZipInfo(prefix+rel,date_time=(2026,10,15,0,0,0))
            zi.compress_type=zipfile.ZIP_DEFLATED
            zi.external_attr=(stat.S_IFREG | 0o644)<<16
            z.writestr(zi,data,compress_type=zipfile.ZIP_DEFLATED,compresslevel=8)

def manifests(files:dict[str,bytes])->dict[str,bytes]:
    out=dict(files)
    out.pop('FILE_HASHES.json',None);out.pop('SHA256SUMS.txt',None)
    checks={name:sha(body) for name,body in sorted(out.items())}
    out['FILE_HASHES.json']=(json.dumps({'schema':'aoi.source.hashes.v1','files':checks},indent=2,sort_keys=True,ensure_ascii=False)+'\n').encode()
    out['SHA256SUMS.txt']=(''.join(f'{sha(b)}  {name}\n' for name,b in sorted(out.items()) if name!='SHA256SUMS.txt')).encode()
    return out
