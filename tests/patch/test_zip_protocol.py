#!/usr/bin/env python3
"""M00 ZIP protocol smoke, including negative ownership/base/dependency checks."""
import json
from pathlib import Path
import shutil,subprocess,tempfile,sys,zipfile,re
root=Path(__file__).resolve().parents[2]
# Choose the next valid release relative to the source under test, not a fixed r002.
current_release=json.loads((root/'RELEASE.json').read_text(encoding='utf-8'))['release_id']
match=re.fullmatch(r'(M\d{2}-r)(\d{3})',current_release)
if not match or int(match.group(2)) >= 999:
    raise ValueError('cannot determine valid next release ID for ZIP protocol test')
next_release=f'{match.group(1)}{int(match.group(2))+1:03d}'
tools=root/'tools'

def cmd(*a,expect=0):
 r=subprocess.run([sys.executable,*map(str,a)],capture_output=True,text=True)
 if (r.returncode==0)!=(expect==0):
  raise AssertionError(f'expected exit {expect}, got {r.returncode}:\n{r.stdout}\n{r.stderr}')
 return r

with tempfile.TemporaryDirectory(prefix='aoi_patch_test_') as tmp:
 t=Path(tmp);base=t/'base.zip';work=t/'work';out=t/'patch.zip';candidate=t/'candidate.zip'
 cmd(tools/'package_source.py','--root',root,'--out',base)
 def ignore_generated(directory,names):
  selected={'__pycache__'} & set(names)
  if Path(directory).resolve()==root: selected.update({'build','runs','evidence'} & set(names))
  return selected
 shutil.copytree(root,work,ignore=ignore_generated)
 # Authorized A2 modification: should make a patch even before W002 verification, but cannot integrate yet.
 f=work/'libs/plc/README.md';f.write_text(f.read_text()+'\nSIM TEST PATCH\n',encoding='utf-8')
 cmd(tools/'create_patch.py','--base',base,'--worktree',work,'--agent','AOI-Agent02','--tasks','W006','--out',out)
 cmd(tools/'validate_patch.py','--base',base,'--patch',out)
 cmd(tools/'integrate_patch.py','--base',base,'--patch',out,'--out',candidate,'--release-id',next_release,expect=1)
 # Unowned edit must fail (agent02 attempts to write UI).
 (work/'resources/qml/Main.qml').write_text('// unauthorized modification\n',encoding='utf-8')
 cmd(tools/'create_patch.py','--base',base,'--worktree',work,'--agent','AOI-Agent02','--tasks','W006','--out',t/'bad_ownership.zip',expect=1)
 (work/'resources/qml/Main.qml').write_bytes((root/'resources/qml/Main.qml').read_bytes())
 # Agent cannot report someone else's task as its own.
 cmd(tools/'create_patch.py','--base',base,'--worktree',work,'--agent','AOI-Agent02','--tasks','W011','--out',t/'wrong_wbs.zip')
 cmd(tools/'validate_patch.py','--base',base,'--patch',t/'wrong_wbs.zip',expect=1)
 # ZIP slip / path traversal must be rejected before reading content.
 malicious=t/'malicious.zip'
 with zipfile.ZipFile(malicious,'w') as z:
  z.writestr('patch.json','{}')
  z.writestr('../outside.txt','attack')
 cmd(tools/'validate_patch.py','--base',base,'--patch',malicious,expect=1)
 # In test-only modified base, mark prerequisite W002 VERIFIED to exercise positive integration.
 led=work/'docs/agents/TASK_LEDGER.json';obj=json.loads(led.read_text())
 obj['items']['W001']['status']='VERIFIED';obj['items']['W002']['status']='VERIFIED'
 led.write_text(json.dumps(obj,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
 base2=t/'base2.zip';patch2=t/'patch2.zip'
 cmd(tools/'package_source.py','--root',work,'--out',base2)
 cmd(tools/'create_patch.py','--base',base2,'--worktree',work,'--agent','AOI-Agent02','--tasks','W006','--out',patch2,expect=1) # unchanged files vs base2 => reject
 # Create modified worktree built from verified synthetic base
 (work/'libs/plc/README.md').write_text(f.read_text()+'MORE PATCH\n',encoding='utf-8')
 cmd(tools/'create_patch.py','--base',base2,'--worktree',work,'--agent','AOI-Agent02','--tasks','W006','--out',patch2)
 cmd(tools/'validate_patch.py','--base',base2,'--patch',patch2)
 cmd(tools/'validate_patch.py','--base',base,'--patch',patch2,expect=1) # wrong original SHA
 cmd(tools/'integrate_patch.py','--base',base2,'--patch',patch2,'--out',candidate,'--release-id',next_release)
 from zipfile import ZipFile
 with ZipFile(candidate) as z:
  release=json.loads(z.read(f'AOI-Vision-Platform-{next_release}/RELEASE.json'))
  assert release['status']=='CANDIDATE_UNVERIFIED'
 print('PASS: ZIP patch positive verified + ownership reject + WBS-owner reject + base-SHA reject + prerequisite reject + ZIP-slip reject + candidate unverified')
