#!/usr/bin/env python3
"""No-sdk source package deterministic and negative tests; no A0 release claim."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

arg=sys.argv[1] if len(sys.argv)>1 else 'tools/package_source_verified.py'
sp=importlib.util.spec_from_file_location("a10_source_package",arg)
mod=importlib.util.module_from_spec(sp);sp.loader.exec_module(mod)

class SourcePackageTest(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory();self.addCleanup(self.tmp.cleanup)
        self.root=Path(self.tmp.name)/"repo";self.root.mkdir()
        def git(*a):
            subprocess.run(["git",*a],cwd=self.root,check=True,capture_output=True)
        git("init","-q")
        git("config","user.name","A10-Testing")
        git("config","user.email","a10-test@invalid.example")
        for name,body in (("alpha.py",b"print('safe')\n"),("beta.cpp",b"int main(){return 0;}\n"),
                          ("README.md",b"SIM_ONLY\n"),("LICENSE",b"test fixture\n"),("test.txt",b"123\n")):
            (self.root/name).write_bytes(body)
        git("add",".");git("commit","-qm","baseline")
        self.git=git
    def test_source_zip_reproducible_git_locked(self):
        a=mod.package(self.root,Path(self.tmp.name)/"out1")
        b=mod.package(self.root,Path(self.tmp.name)/"out2")
        self.assertEqual(a.read_bytes(),b.read_bytes())
        import zipfile
        with zipfile.ZipFile(a) as f:
            self.assertIsNone(f.testzip())
            manifest=json.loads(f.read("AOI_SOURCE_PROVENANCE.json"))
            self.assertFalse(manifest["release_authorized"])
            self.assertEqual(manifest["tracked_file_count"],5)
            self.assertEqual(len(f.namelist()),6)
    def test_dirty_file_fail_closed(self):
        (self.root/"alpha.py").write_text("changed")
        with self.assertRaises(mod.PackageError):mod.package(self.root,Path(self.tmp.name)/"out")
    def test_symlink_fail_closed(self):
        (self.root/"danger-link").symlink_to("/etc/passwd")
        self.git("add","danger-link");self.git("commit","-qm","link")
        with self.assertRaises(mod.PackageError):mod.package(self.root,Path(self.tmp.name)/"out")
    def test_output_inside_repo_fail_closed(self):
        with self.assertRaises(mod.PackageError):mod.package(self.root,self.root/"dist")
    def test_binary_lfs_placeholder_rejected(self):
        (self.root/"alpha.py").write_bytes(b"version https://git-lfs.github.com/spec/v1\n")
        self.git("add","alpha.py");self.git("commit","-qm","pointer")
        with self.assertRaises(mod.PackageError):mod.package(self.root,Path(self.tmp.name)/"out")
if __name__=="__main__":unittest.main(argv=[sys.argv[0],"-v"])
