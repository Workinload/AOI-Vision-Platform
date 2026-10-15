#!/usr/bin/env python3
"""Deterministic Draft source ZIP from a clean exact Git commit, not a release.

Intentionally excludes Qt/OpenCV SDK, local test fixtures, credentials, untracked
files and .git. A0 release approval is a separate gate. Uses only stdlib+Git.
"""
from __future__ import annotations
import argparse
import csv
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import zipfile

STAMP=(2020,1,1,0,0,0)
FORBIDDEN_NAMES={".env",".env.production","id_rsa","id_ed25519","github_token"}
class PackageError(ValueError):pass

def run(root,*args):
    p=subprocess.run(["git",*args],cwd=root,capture_output=True)
    if p.returncode:raise PackageError("git "+" ".join(args)+" exit "+str(p.returncode)+": "+p.stderr.decode(errors="replace")[-150:])
    return p.stdout

def sha(data):return hashlib.sha256(data).hexdigest()

def package(root,outdir):
    root=root.resolve();outdir=outdir.resolve()
    if outdir==root or outdir.is_relative_to(root):raise PackageError("output directory must be outside source")
    head=run(root,"rev-parse","HEAD").decode().strip()
    if len(head)!=40:raise PackageError("HEAD not 40-digit SHA")
    if run(root,"diff","--name-only") or run(root,"diff","--cached","--name-only"):
        raise PackageError("dirty tracked working tree: no credible exact-head package")
    records=run(root,"ls-files","-z").split(b"\0")
    names=[os.fsdecode(p) for p in records if p]
    if len(names)!=len(set(names)) or len(names)<5:raise PackageError("duplicate/few tracked paths")
    if any(name.startswith("/") or ".." in Path(name).parts or name.startswith(".git/")
           for name in names):raise PackageError("unsafe source archive paths")
    manifest={}
    for name in sorted(names):
        file=root/name
        if (file.is_symlink() or not file.is_file() or
            any(n.lower() in FORBIDDEN_NAMES for n in file.parts) or
            ".git" in file.parts):
            raise PackageError("symlink or forbidden tracked source path: "+name)
        size=file.stat().st_size
        if size>100*1024*1024:raise PackageError("refuse binary/SDK file over 100MiB: "+name)
        content=file.read_bytes()
        if content.startswith(b"version https://git-lfs.github.com/spec/v1"):
            raise PackageError("LFS placeholder is not actual source: "+name)
        blob=run(root,"rev-parse","HEAD:"+name).decode().strip()
        local_blob=run(root,"hash-object","--",name).decode().strip()
        if blob!=local_blob:raise PackageError("file differs from Git blob "+name)
        manifest[name]={"bytes":size,"sha256":sha(content),"git_blob":blob}
    orig_manifest=root/"docs/plan/ORIGINAL_PLAN_SHA256_MANIFEST.json"
    if orig_manifest.exists():
        j=json.loads(orig_manifest.read_text(encoding="utf-8"))
        locked=j["original_files"]
        if len(locked)!=55:raise PackageError("original PLAN not 55 files")
        for name,expected in locked.items():
            f="docs/plan/v1.1-original/"+name
            if f not in manifest or manifest[f]["sha256"]!=expected:
                raise PackageError("immutable PLAN file changed: "+f)
    safety=root/"reference/plan_v1.1/FIELD_GATES_14.csv"
    if safety.exists():
        with safety.open(encoding="utf-8-sig",newline="") as f:records=list(csv.DictReader(f))
        if len(records)!=14 or any(x["ProductionWriteAllowed"]!="FALSE" for x in records):
            raise PackageError("production field gate unexpectedly enabled")
    outdir.mkdir(parents=True,exist_ok=True)
    target=outdir/("AOI_DEV_DRAFT_SOURCE_"+head[:12]+".zip")
    note={"schema":"aoi.a10.dev-source-manifest.v1", "commit_sha":head,
          "scope":"GIT_HEAD_TRACKED_SOURCES_ONLY", "release_authorized":False,
          "sim_ready":False,"field_accepted":False,
          "windows_native_verified":False,
          "source_paths":manifest,"tracked_file_count":len(manifest)}
    tmp=target.with_suffix(".zip.tmp")
    try:
        with zipfile.ZipFile(tmp,"w",compression=zipfile.ZIP_DEFLATED,compresslevel=6) as z:
            for name in sorted(manifest):
                zi=zipfile.ZipInfo("source/"+name,date_time=STAMP)
                zi.compress_type=zipfile.ZIP_DEFLATED
                zi.external_attr=(0o100644)<<16
                z.writestr(zi,(root/name).read_bytes(),compress_type=zipfile.ZIP_DEFLATED,compresslevel=6)
            zi=zipfile.ZipInfo("AOI_SOURCE_PROVENANCE.json",date_time=STAMP)
            zi.compress_type=zipfile.ZIP_DEFLATED
            zi.external_attr=(0o100644)<<16
            z.writestr(zi,json.dumps(note,ensure_ascii=False,sort_keys=True,indent=2).encode()+b"\n")
        with zipfile.ZipFile(tmp) as z:
            if z.testzip() is not None or len(z.namelist())!=len(manifest)+1:
                raise PackageError("ZIP CRC/count invalid")
        os.replace(tmp,target)
    finally:
        if tmp.exists():tmp.unlink()
    digest=sha(target.read_bytes())
    (outdir/(target.name+".sha256")).write_text(digest+"  "+target.name+"\n",encoding="utf-8")
    print(json.dumps({"status":"PASS","scope":"DRAFT_SOURCE_NOT_RELEASE", "source_sha":head,
                      "archive":str(target),"file_count":len(manifest),"zip_sha256":digest,
                      "sdk_included":False,"release_approved":False}))
    return target

def main():
    p=argparse.ArgumentParser()
    p.add_argument("--source-root",type=Path,required=True)
    p.add_argument("--output-dir",type=Path,required=True)
    args=p.parse_args()
    try:package(args.source_root,args.output_dir);return 0
    except (PackageError,FileNotFoundError,KeyError) as e:
        print("DRAFT_SOURCE_PACKAGE_FAIL_CLOSED",str(e),file=sys.stderr);return 2

if __name__=="__main__":sys.exit(main())