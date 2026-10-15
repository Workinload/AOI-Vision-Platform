#!/usr/bin/env python3
"""Fail-closed bootstrap negative cases only. Real PLC driver is absent."""
from pathlib import Path
import argparse,subprocess,tempfile,socket,os,time
p=argparse.ArgumentParser();p.add_argument('--bin-dir',required=True)
a=p.parse_args();b=Path(a.bin_dir)
with tempfile.TemporaryDirectory(prefix='aoi_negative_') as t:
    t=Path(t)
    bad=subprocess.run([str(b/'vplc'),'--bind','0.0.0.0','--port','19600'],capture_output=True,timeout=3)
    assert bad.returncode!=0,'vPLC wrongly accepted non-loopback bind'
    bad=subprocess.run([str(b/'aoi-core'),'--role','ADMIN','--data-dir',str(t/'admin')],capture_output=True,timeout=3)
    assert bad.returncode!=0,'Core accepted unrecognized role'
    master=t/'production_master'
    pr=subprocess.Popen([str(b/'aoi-core'),'--role','master','--data-dir',str(master)],stdout=subprocess.DEVNULL,stderr=subprocess.PIPE)
    try:
        deadline=time.monotonic()+4
        while not (master/'core.ipc.sock').exists():
            assert time.monotonic()<deadline and pr.poll() is None,'Core bootstrap failed'
            time.sleep(.04)
        bad=subprocess.run([str(b/'aoi-core'),'--role','slave','--data-dir',str(master)],capture_output=True,timeout=4)
        assert bad.returncode!=0,'Slave could write into live master directory'
    finally:
        pr.terminate();pr.wait(timeout=3)
    bad=subprocess.run([str(b/'aoi-core'),'--role','slave','--data-dir',str(master)],capture_output=True,timeout=4)
    assert bad.returncode!=0,'Slave accepted master-only DB after restart'
print('PASS: remote vPLC refused; invalid Core role refused; shared live DB refused; cross-role DB reuse refused')
