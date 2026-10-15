#!/usr/bin/env python3
"""M00 Linux smoke: real five subprocesses, Qt Quick load, local IPC and UDP HEALTH only."""
import argparse
import json
import os
from pathlib import Path
import socket
import sqlite3
import subprocess
import tempfile
import time
import sys
import struct
import zlib

p=argparse.ArgumentParser()
p.add_argument('--bin-dir',required=True)
p.add_argument('--evidence-dir',default=None)
a=p.parse_args()
exe=Path(a.bin_dir).resolve()
run=Path(a.evidence_dir).resolve() if a.evidence_dir else Path(tempfile.mkdtemp(prefix='aoi_m00_'))
run.mkdir(parents=True,exist_ok=True)
logs=run/'logs';logs.mkdir(exist_ok=True)
procs=[];log_handles=[]

def launch(name,command):
    f=(logs/f'{name}.log').open('w',encoding='utf-8')
    log_handles.append(f)
    pr=subprocess.Popen([str(i) for i in command],stdout=f,stderr=subprocess.STDOUT,env=os.environ.copy())
    procs.append((name,pr))
    return pr

def read_health(path):
    with socket.socket(socket.AF_UNIX,socket.SOCK_STREAM) as s:
        s.settimeout(1.5)
        s.connect(str(path))
        s.sendall(b'health\n')
        response=b''
        while not response.endswith(b'\n'):
            block=s.recv(4096)
            if not block:raise ConnectionError('Core IPC closed without complete health response')
            response+=block
            if len(response)>65536:raise ValueError('health response is over 64KiB')
        return json.loads(response.decode())

def check(condition,msg):
    if not condition:raise AssertionError(msg)

try:
    master=run/'master'; slave=run/'slave';
    udp=socket.socket(socket.AF_INET,socket.SOCK_DGRAM)
    udp.bind(('127.0.0.1',0));port=udp.getsockname()[1]
    udp.close() # free port to bind vPLC, then use a fresh ephemeral client for probe
    udp=socket.socket(socket.AF_INET,socket.SOCK_DGRAM)
    launch('vplc',[exe/'vplc','--port',port])
    launch('master-core',[exe/'aoi-core','--role','master','--data-dir',master])
    launch('slave-core',[exe/'aoi-core','--role','slave','--data-dir',slave])
    up=False
    for _ in range(70):
        check(all(pr.poll() is None for _,pr in procs),'process exited prematurely')
        if (master/'core.ipc.sock').exists() and (slave/'core.ipc.sock').exists():
            try:
                m=read_health(master/'core.ipc.sock');s=read_health(slave/'core.ipc.sock')
                up=True;break
            except (OSError,ValueError):pass
        time.sleep(.08)
    check(up,'Core IPC did not start')
    check(m['role']=='MASTER' and s['role']=='SLAVE','role mismatch')
    check(m['db_file']!=s['db_file'],'shared database unsafe')
    check(m['mode']=='SIM_ONLY' and s['mode']=='SIM_ONLY','SIM mode absent')
    check(m['state']=='SCAFFOLD_ONLY' and s['state']=='SCAFFOLD_ONLY','scaffold misrepresented')
    check(m['fins']=='NOT_IMPLEMENTED' and s['camera']=='NOT_IMPLEMENTED','untrue advertised implementation')
    with socket.socket(socket.AF_UNIX,socket.SOCK_STREAM) as client:
        client.settimeout(2);client.connect(str(master/'core.ipc.sock'))
        client.sendall(b'write D1016 1\n')
        response=client.recv(2048)
        check(json.loads(response)['status']=='REJECTED','unsafe IPC write accepted')
    with sqlite3.connect(master/'sim_bootstrap.sqlite3') as d1,sqlite3.connect(slave/'sim_bootstrap.sqlite3') as d2:
        check(d1.execute('PRAGMA journal_mode').fetchone()[0]=='wal','Master WAL absent')
        check(d2.execute('PRAGMA journal_mode').fetchone()[0]=='wal','Slave WAL absent')
        check(d1.execute("select value from m00_meta where name='host_role'").fetchone()[0]=='MASTER','Master DB mismatch')
        check(d2.execute("select value from m00_meta where name='host_role'").fetchone()[0]=='SLAVE','Slave DB mismatch')
    udp.settimeout(2)
    udp.sendto(b'M00_HEALTH_V1',('127.0.0.1',port))
    answer,_=udp.recvfrom(200)
    check(answer==b'M00_SIM_ONLY_FINS_NOT_IMPLEMENTED','vPLC status mismatch')
    udp.settimeout(.6)
    udp.sendto(b'FINS_READ_WRITE_NOT_ALLOWED',('127.0.0.1',port))
    try:udp.recvfrom(256);raise AssertionError('vPLC responded to nonhealth datagram')
    except socket.timeout:pass
    launch('master-ui',[exe/'aoi-ui','--role','master','--ipc',master/'core.ipc.sock','--screenshot',run/'master_m00.png'])
    launch('slave-ui',[exe/'aoi-ui','--role','slave','--ipc',slave/'core.ipc.sock','--screenshot',run/'slave_m00.png'])
    ok=False
    for _ in range(70):
        check(all(pr.poll() is None for _,pr in procs),'one of five processes crashed')
        contentM=(logs/'master-ui.log').read_text(errors='replace')
        contentS=(logs/'slave-ui.log').read_text(errors='replace')
        if 'AOI_UI_IPC_CONNECTED MASTER' in contentM and 'AOI_UI_IPC_CONNECTED SLAVE' in contentS:
            ok=True; break
        time.sleep(.12)
    check(ok,'Qt Quick UI did not load/connect real Core IPC: check logs '+str(logs))
    deadline=time.monotonic()+8
    def image_finished(path):
        # Strict PNG: no missing, truncated or CRC-damaged screenshots may pass QA.
        if not path.exists() or path.stat().st_size<1024:return False
        data=path.read_bytes()
        if data[:8]!=b'\x89PNG\r\n\x1a\n':return False
        offset=8;chunks=[];image_dims=None
        while offset+12<=len(data):
            length=struct.unpack_from('>I',data,offset)[0]
            if length>16_777_216 or offset+length+12>len(data):return False
            body=data[offset+4:offset+8+length]
            crc=struct.unpack_from('>I',data,offset+8+length)[0]
            if zlib.crc32(body)&0xffffffff!=crc:return False
            kind=body[:4];chunks.append(kind)
            if kind==b'IHDR':
                if length!=13:return False
                image_dims=struct.unpack_from('>II',body,4)
            offset+=length+12
            if kind==b'IEND':break
        return (offset==len(data) and len(chunks)>=3 and chunks[0]==b'IHDR'
                and chunks[-1]==b'IEND' and b'IDAT' in chunks
                and image_dims is not None and all(value>=32 for value in image_dims))
    while not (image_finished(run/'master_m00.png') and image_finished(run/'slave_m00.png')):
        if time.monotonic()>deadline:break
        time.sleep(.09)
    screenshots=image_finished(run/'master_m00.png') and image_finished(run/'slave_m00.png')
    check(screenshots,'Qt Quick screenshot missing, truncated, or not a valid PNG')
    check(len(procs)==5 and all(pr.poll() is None for _,pr in procs),'five independent processes not alive')
    (run/'test_evidence.json').write_text(json.dumps({'status':'PASS','five_live_pids':{name:pr.pid for name,pr in procs},'master':m,'slave':s,'vplc_udp_health':'PASS','unsupported_udp_ignored':True,'ipc_denies_write':True,'database_role_isolation':True,'linux_qml_load':'PASS','screenshots':'PASS','scope':'M00_SCAFFOLD_ONLY_NOT_FINS'},indent=2,ensure_ascii=False))
    print('PASS: 5 OS processes / 2 role-specific Core DB / Qt Quick UI real IPC / UDP health / deny-write; screenshots='+('PASS' if screenshots else 'NOT_VERIFIED'))
    print('EVIDENCE_DIR='+str(run))
except Exception:
    print('SMOKE FAILED. LOG DIR='+str(logs),file=sys.stderr)
    for f in logs.glob('*.log'):
        print('===',f.name,'===\n',f.read_text(errors='replace')[-6500:],file=sys.stderr)
    raise
finally:
    for _,pr in reversed(procs):
        if pr.poll() is None:pr.terminate()
    for _,pr in procs:
        try:pr.wait(timeout=3)
        except subprocess.TimeoutExpired:pr.kill();pr.wait()
    for f in log_handles:f.close()
    if 'udp' in locals():udp.close()
