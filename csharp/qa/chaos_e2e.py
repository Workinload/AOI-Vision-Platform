#!/usr/bin/env python3
"""Five OS processes; QA FINS fixture changes end barcode after first read; HOLD mandatory."""
import argparse,os,pathlib,socket,subprocess,sys,tempfile,time,sqlite3
from integrity_audit import verify,verify_eleven_channels
def fins(address):
    req=bytes([128,0,2,0,1,0,0,99,0,41,1,1,130,address>>8,address&255,0,0,1])
    with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as s:
        s.settimeout(1)
        s.sendto(req,('127.0.0.1',19600))
        p,remote=s.recvfrom(64)
        assert remote[0]=='127.0.0.1' and len(p)==16 and p[9]==41 and p[12:14]==b'\x00\x00'
        return p[14]*256+p[15]
def main():
    p=argparse.ArgumentParser()
    p.add_argument('--dotnet',default='dotnet')
    p.add_argument('--evidence-root')
    p.add_argument('--all-channels',action='store_true',help='Full 11-channel barcode flip must HOLD')
    p.add_argument('--configuration',default='Debug')
    p.add_argument('--timeout',type=int,default=40)
    a=p.parse_args()
    root=pathlib.Path(__file__).resolve().parent.parent
    evidence=pathlib.Path(a.evidence_root).resolve() if a.evidence_root else pathlib.Path(tempfile.mkdtemp(prefix='aoi-a5-chaos-'))
    evidence.mkdir(parents=True,exist_ok=True)
    host=root/f'src/AOI.Host/bin/{a.configuration}/net10.0/AOI.Host.dll'
    ui=root/f'src/AOI.Ui.Avalonia/bin/{a.configuration}/net10.0/AOI.Ui.Avalonia.dll'
    if not host.is_file() or not ui.is_file(): raise RuntimeError('build .NET solution before chaos')
    processes=[];files=[]
    def start(name,*args):
        f=(evidence/(name+'.log')).open('w',encoding='utf8')
        files.append(f)
        env={**os.environ,'DOTNET_CLI_TELEMETRY_OPTOUT':'1'}
        if a.all_channels:
            env['AOI_SIM_CHANNEL_SET']='ALL_11'
        else:
            env.pop('AOI_SIM_CHANNEL_SET',None)
        proc=subprocess.Popen(args,cwd=root,env=env,stdout=f,stderr=subprocess.STDOUT)
        processes.append((name,proc))
    try:
        start('fault-vplc',sys.executable,'-u',str(pathlib.Path(__file__).with_name('faulty_vplc.py')))
        for role in ('slave','master'):
            start(role+'-core',a.dotnet,str(host),role,'--data',str(evidence))
        for role in ('master','slave'):
            start(role+'-ui',a.dotnet,str(ui),'--'+role,'--headless','--once')
        print('A5_FIVE_OS_PROCESSES_STARTED',flush=True)
        deadline=time.monotonic()+a.timeout; last='no observation'
        while time.monotonic()<deadline:
            for name,proc in processes[:3]:
                if proc.poll() is not None: raise RuntimeError(name+' unexpectedly exited '+str(proc.returncode))
            try:
                if a.all_channels:
                    verify_eleven_channels(evidence,True)
                else:
                    verify(evidence,True)
                for role in ('master','slave'):
                    item=next(x for x in processes if x[0]==role+'-ui')[1]
                    assert item.poll()==0,role+' UI not finished'
                    assert 'UI_HEADLESS_SNAPSHOT_OK' in (evidence/(role+'-ui.log')).read_text(),role+' UI IPC missing'
                assert 'END_BARCODE_MISMATCH_OR_MISSING' in (evidence/'master-core.log').read_text(),'no HOLD log'
                assert 'QA_BARCODE_FLIPPED' in (evidence/'fault-vplc.log').read_text(),'no actual flip'
                assert (fins(1999),fins(1016),fins(2099))==(2,0,0),'virtual release output unsafe'
                time.sleep(0.4)
                if a.all_channels:
                    verify_eleven_channels(evidence,True)
                    print('A5_CHAOS_11CH_PASS false-release=0 durable-peer-ack=7 '
                          'results=11 assets=13 UI=2 SIM_ONLY',flush=True)
                else:
                    verify(evidence,True)
                    print('A5_CHAOS_PASS false-release=0 durable-peer-ack=2 UI=2 SIM_ONLY',flush=True)
                return 0
            except (AssertionError,KeyError,ValueError,OSError,sqlite3.Error,TypeError) as ex:
                last=str(ex)
            time.sleep(0.2)
        raise RuntimeError('timeout; last='+last)
    except Exception as ex:
        print('A5_CHAOS_FAIL '+str(ex),file=sys.stderr)
        for name,_ in processes:
            path=evidence/(name+'.log')
            if path.is_file(): print(name+': '+path.read_text()[-1500:],file=sys.stderr)
        return 1
    finally:
        for _,p in reversed(processes):
            if p.poll() is None:p.terminate()
        for _,p in processes:
            try:p.wait(timeout=4)
            except subprocess.TimeoutExpired:p.kill();p.wait()
        for f in files:f.close()
        print('A5_EVIDENCE_ROOT='+str(evidence),flush=True)
if __name__=='__main__':sys.exit(main())
