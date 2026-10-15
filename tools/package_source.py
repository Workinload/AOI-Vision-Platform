#!/usr/bin/env python3
"""Package a SOURCE archive from an M00 workspace (never include SDKs/build/logs)."""
import argparse
from pathlib import Path
from zip_common import all_tree_files, manifests, archive_bytes, source_members, file_sha
p=argparse.ArgumentParser();p.add_argument('--root',required=True);p.add_argument('--out',required=True)
a=p.parse_args()
files=manifests(all_tree_files(Path(a.root)))
release_name=__import__('json').loads(files['RELEASE.json'])['release_id']
out=Path(a.out)
archive_bytes(f'AOI-Vision-Platform-{release_name}/',files,out)
source_members(out)
print('PASS SOURCE ZIP',out,'files',len(files),'sha256',file_sha(out))
