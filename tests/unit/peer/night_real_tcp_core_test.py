import json,os,socket,tempfile,subprocess,time,sqlite3,pathlib,hashlib,sys,argparse
parser=argparse.ArgumentParser();parser.add_argument("--bin-dir",default="build/linux-sim");parser.add_argument("--evidence-dir",default="");args=parser.parse_args()
bin=str(pathlib.Path(args.bin_dir).resolve()/'aoi-core')
root=pathlib.Path(args.evidence_dir) if args.evidence_dir else pathlib.Path(tempfile.mkdtemp(prefix='aoi-night01-'))
root.mkdir(parents=True,exist_ok=True); logs=[]; processes=[]
E=lambda id,wp,ch,q='OK',status='SUCCEEDED',epoch=2,attempt=1:dict(event_id=id,workpiece_id=wp,release_id='SIM-REL-0001',channel_id=ch,capture_id='cap-'+id,epoch=epoch,attempt_no=attempt,processing_status=status,quality_status=q)
fixture=dict(schema='aoi.sim.fixture.v0',pieces=[dict(workpiece_id='SIM-TEST-A',barcode='REUSED-BC',release_id='SIM-REL-0001',epoch=2),dict(workpiece_id='SIM-TEST-B',barcode='REUSED-BC',release_id='SIM-REL-0001',epoch=2)],events=[E('old','SIM-TEST-A','CH02',epoch=1)]+[E('A-'+str(i),'SIM-TEST-A',f'CH{i:02}',q='UNDECIDED' if i==1 else 'NG' if i==8 else 'OK') for i in range(1,12)]+[E('B-'+str(i),'SIM-TEST-B',f'CH{i:02}',q='UNDECIDED' if i==1 else 'OK') for i in range(1,10)]+[E('B-timeout','SIM-TEST-B','CH10',q='MISSING',status='TIMEOUT')])
f=root/'fixture.json';f.write_text(json.dumps(fixture))
def start(role,port=0,withfixture=False,demo=False):
 d=root/role;d.mkdir(exist_ok=True);args=[bin,'--role',role,'--data-dir',str(d),'--peer-port',str(port)];
 if withfixture:args+=['--sim-fixture',str(f)]
 if demo:args+=['--peer-demo-event',role+'-message-1']
 log=open(root/(role+'.log'),'w');logs.append(log);p=subprocess.Popen(args,stdout=log,stderr=subprocess.STDOUT);processes.append(p)
 for i in range(200):
  if (d/'core.ipc.sock').exists():break
  if p.poll()!=None:raise AssertionError(f'early exit {role}={p.returncode} '+(root/(role+'.log')).read_text())
  time.sleep(.03)
 return p,d

def ipc(d,cmd):
 with socket.socket(socket.AF_UNIX,socket.SOCK_STREAM) as s:
  s.settimeout(5);s.connect(str(d/'core.ipc.sock'));s.sendall(cmd+b'\n');data=b''
  while b'\n' not in data:data+=s.recv(20000)
  return json.loads(data.split(b'\n')[0])
try:
 master,m=start('master',port=29172,withfixture=True,demo=True);slave,s=start('slave',port=29172,demo=True)
 tests={}
 tests['health']=ipc(m,b'health')['mode']=='SIM_ONLY'
 snap=ipc(m,b'preview_snapshot');tests['11_channels']=len(snap['channels'])==11
 tests['missing_hold']=snap['final']['outcome']=='HOLD'
 a=ipc(m,b'preview_snapshot SIM-TEST-A')
 tests['ng_master']=a['final']['outcome']=='NG'
 tests['ng_reason']=a['final']['reason']=='defect_ng'
 tests['stale_epoch_quarantined']=snap['rejected_events']>=1
 tests['missing_invalid_write']=ipc(m,b'plc_write D1000 1')['status']=='REJECTED'
 tests['slave_no_final']=ipc(s,b'preview_snapshot')['final']['outcome']=='HOLD'
 tests['cross_role_isolated']=ipc(s,b'preview_snapshot')['workpiece_id']!=snap['workpiece_id']
 tests['versioned_snapshot']=snap['schema']=='aoi.preview.snapshot.v0'
 tests['separate_dbs']=(m/'sim_bootstrap.sqlite3').exists() and (s/'sim_bootstrap.sqlite3').exists()
 for i in range(80):
  with sqlite3.connect(m/'sim_bootstrap.sqlite3') as db: master_recv=db.execute('select count(*) from peer_inbox').fetchone()[0]; pending=db.execute('select count(*) from peer_outbox where ack=0').fetchone()[0]
  with sqlite3.connect(s/'sim_bootstrap.sqlite3') as db: slave_recv=db.execute('select count(*) from peer_inbox').fetchone()[0]; sp=db.execute('select count(*) from peer_outbox where ack=0').fetchone()[0]
  if master_recv==1 and slave_recv==1 and pending==0 and sp==0:break
  time.sleep(.1)
 tests['real_tcp_to_master']=master_recv==1
 with sqlite3.connect(m/'sim_bootstrap.sqlite3') as db: tests['transactional_peer_effect']=db.execute('select count(*) from peer_effects').fetchone()[0]==1
 tests['real_tcp_to_slave']=slave_recv==1
 tests['real_ack_clears_outbox']=pending==0 and sp==0
 # verify idempotent via restart slave and same id, matching payload
 slave.terminate();slave.wait(4);slave2,s2=start('slave',port=29172,demo=True)
 time.sleep(1.1)
 with sqlite3.connect(m/'sim_bootstrap.sqlite3') as db: tests['replay_no_double_inbox']=db.execute('select count(*) from peer_inbox').fetchone()[0]==1 and db.execute('select count(*) from peer_effects').fetchone()[0]==1
 with sqlite3.connect(s/'sim_bootstrap.sqlite3') as db: tests['restart_outbox_acked']=db.execute('select count(*) from peer_outbox where ack=0').fetchone()[0]==0
 for k,v in tests.items():print(('PASS' if v else 'FAIL')+' '+k,flush=True)
 print('SIM_TESTS',sum(tests.values()),'/',len(tests),'MASTER_RECV',master_recv,'SLAVE_RECV',slave_recv,'root',root)
 (root/'results.json').write_text(json.dumps(tests,indent=2))
 import hashlib
 with (root/'SHA256SUMS.txt').open('w') as h:
  for file in sorted(root.glob('*')):
   if file.is_file() and file.name!='SHA256SUMS.txt':h.write(hashlib.sha256(file.read_bytes()).hexdigest()+'  '+file.name+'\n')
 sys.exit(0 if all(tests.values()) else 1)
finally:
 for p in processes:
  if p.poll() is None:p.terminate()
 for p in processes:
  try:p.wait(4)
  except:p.kill()
 for log in logs:log.close()