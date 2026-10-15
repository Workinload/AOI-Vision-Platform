#!/usr/bin/env python3
"""W003 latest HEAD 9 SHA256 and actual Git blob identity; unapproved DAG only."""
import argparse,csv,hashlib,json,subprocess,sys
from pathlib import Path

def check(b,msg):
    if not b:raise AssertionError(msg)

def main():
    ap=argparse.ArgumentParser()
    ap.add_argument("--repo",type=Path,required=True)
    ap.add_argument("--sha",required=True)
    ap.add_argument("--round",type=int,required=True)
    a=ap.parse_args();r=a.repo.resolve()
    run=lambda *v:subprocess.check_output(["git",*v],cwd=r,text=True).strip()
    check(run("rev-parse","HEAD")==a.sha,"PINNED_HEAD_MISMATCH")
    manifest=json.loads((r/"docs/reviews/W003_A0_20261016_FIXED_VALIDATION_MANIFEST.json").read_text(encoding="utf-8"))
    entries=manifest["file_sha256"]
    check(len(entries)==9,"expected nine original candidate evidence files")
    for path,expected in sorted(entries.items()):
        rel="docs/reviews/"+path
        f=r/rel
        check(f.is_file(),"missing "+rel)
        actual=hashlib.sha256(f.read_bytes()).hexdigest()
        check(actual==expected,"SHA256_FILE_MISMATCH "+rel)
        blob=run("rev-parse","HEAD:"+rel)
        content_blob=run("hash-object",rel)
        check(blob==content_blob,"GIT_BLOB_DRIFT "+rel)
        print("A10_GIT_BLOB_SHA_PASS",rel,actual,blob)
    orig_plan=json.loads((r/"docs/plan/ORIGINAL_PLAN_SHA256_MANIFEST.json").read_text())
    check(len(orig_plan["original_files"])==55,"original plan count")
    check(orig_plan["archive_sha256"]==manifest["source_reference_sha256"]["original_PLAN_v1.1"],
          "original plan zip SHA drift")
    with (r/"docs/reviews/W003_MODULE_DAG_CANDIDATE.csv").open(encoding="utf-8-sig",newline="") as f:
        dag=list(csv.DictReader(f))
    check(len(dag)==21 and all(row["State"]=="CANDIDATE_UNAPPROVED" for row in dag),
          "false DAG approval")
    edge_count=sum(len([x for x in row["ProposedConsumesProviderModuleIds"].split(";") if x])
                   for row in dag)
    check(edge_count==30,"DAG 30 edge drift")
    with (r/"docs/reviews/W003_MODULE_DAG_NOT_YET_FROZEN.csv").open(encoding="utf-8-sig",newline="") as f:
        untouched=list(csv.DictReader(f))
    check(len(untouched)==21 and all(x["State"]=="CANDIDATE_UNAPPROVED" for x in untouched),
          "old review DAG falsely promoted")
    print("A10_W003_MANIFEST_PASS round="+str(a.round),"file_sha256=9/9","git_blob=9/9",
          "original_PLAN=55","module_count=21","logical_edges=30","FROZEN=FALSE",
          "exact_HEAD="+a.sha)
    return 0

if __name__=="__main__":
    try:sys.exit(main())
    except Exception as e:
        print("A10_W003_MANIFEST_FAIL",type(e).__name__,str(e),file=sys.stderr);raise
