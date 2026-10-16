#!/usr/bin/env python3
"""A5 independent, read-only SIM_ONLY ledger and persisted ACK verifier."""
import argparse,hashlib,json,pathlib,sqlite3,sys,uuid
def check(ok,message):
    if not ok: raise AssertionError(message)
def rows(db,sql):
    return db.execute(sql).fetchall()
def verify(root,hold=False):
    root=pathlib.Path(root).resolve()
    dbs={}
    try:
        for role in ('master','slave'):
            p=root/role/'production.db'
            check(p.is_file(),f'{role} DB missing')
            d=sqlite3.connect(p.as_uri()+'?mode=ro',uri=True)
            d.row_factory=sqlite3.Row
            dbs[role]=d
            check(d.execute('pragma integrity_check').fetchone()[0]=='ok',f'{role} DB corrupt')
            check(d.execute('pragma journal_mode').fetchone()[0]=='wal',f'{role} WAL missing')
        ids={};results={}
        for role,d in dbs.items():
            work=rows(d,'select * from workpieces')
            check(len(work)==1,f'{role} workpiece count')
            w=work[0]; identity=json.loads(w['identity_json'])
            check(str(uuid.UUID(w['workpiece_id']))==identity['workpieceId'] and w['epoch']==identity['epoch'] and w['release_id']==identity['releaseId'] and w['barcode']==identity['barcode'],f'{role} identity mismatch')
            check(identity['epoch']==1 and identity['releaseId']=='SIM_LIGHT_TEST_RELEASE' and identity['barcode']=='SIM-0001',f'{role} unexpected fixture')
            ids[role]=identity
            results[role]={}
            channels={'master':{'CH02','CH06'},'slave':{'CH02'}}[role]
            data=rows(d,'select * from channel_results')
            check(len(data)==len(channels) and {r['channel_id'] for r in data}==channels,f'{role} channels')
            for row in data:
                x=json.loads(row['result_json']); c=x['channelId']
                station,device,owner={'CH02':('ST03','HIK-S-2','slave'),'CH06':('ST05','HIK-M-2','master')}[c]
                check(x['workpieceId']==identity['workpieceId'] and x['traceId']==identity['traceId'] and x['epoch']==identity['epoch'] and x['releaseId']==identity['releaseId'] and x['stationId']==station and x['physicalDeviceId']==device and x['attemptNo']==1 and x['parentFrameId'] is None and x['isSimulated'] is True and x['outcome']=='ok',f'{role}/{c} result mismatch')
                check(row['result_id']==x['resultId'] and row['epoch']==x['epoch'] and row['attempt_no']==1 and row['workpiece_id']==x['workpieceId'],f'{role}/{c} ledger mismatch')
                frame=root/owner/'assets'/uuid.UUID(identity['workpieceId']).hex/c/(uuid.UUID(x['frameId']).hex+'.png')
                check(frame.is_file(),f'{c} missing asset')
                image=frame.read_bytes()
                check(image.startswith(b'\x89PNG\r\n\x1a\n') and hashlib.sha256(image).hexdigest()==x['sourceFrameSha256'],f'{c} tampered asset')
                results[role][c]=x
        check(ids['master']==ids['slave'],'two Core identity mismatch')
        check(results['master']['CH02']==results['slave']['CH02'],'cross-Core CH02 mismatch')
        check(rows(dbs['slave'],'select final_json from workpieces')[0][0] is None,'Slave final forbidden')
        for sender,receiver,typ in (('master','slave','workpiece_registered'),('slave','master','channel_result')):
            out=rows(dbs[sender],'select * from outbox')
            inbox=rows(dbs[receiver],'select * from inbox')
            check(len(out)==len(inbox)==1,f'{sender} ACK/inbox missing')
            a,b=out[0],inbox[0]; env=json.loads(a['envelope_json'])
            check(a['acknowledged_utc'] is not None and a['message_id']==b['message_id']==env['messageId'] and b['sender_node_id']==sender and env['senderNodeId']==sender and env['receiverNodeId']==receiver and env['payloadType']==typ and a['payload_hash']==b['payload_hash']==env['payloadHash']==hashlib.sha256(env['payloadJson'].encode()).hexdigest(),f'{sender} invalid ACK/payload hash')
            payload=json.loads(env['payloadJson'])
            check(payload==(ids[sender] if typ=='workpiece_registered' else results['slave']['CH02']),f'{sender} payload provenance')
            for node,kind in ((receiver,'INBOX_PERSISTED'),(sender,'PERSISTED_ACK_RECEIVED')):
                check(any(x['message_id']==a['message_id'] for x in rows(dbs[node],f"select message_id from flight_events where kind='{kind}'")),f'{node} missing {kind}')
        final=rows(dbs['master'],'select final_json from workpieces')[0][0]
        if hold:
            check(final is None,'HOLD must never create FinalResult')
            check(not rows(dbs['master'],"select * from flight_events where kind='FINAL_DECIDED'"),'unsafe final event')
        else:
            check(final is not None,'missing simulated final')
            x=json.loads(final)
            check(x['disposition']=='good' and x['isSimulation'] is True and 'SIM_ONLY' in x['proof'] and set(x['expected'])=={'CH02','CH06'} and set(x['received'])=={'CH02','CH06'},'unsafe final proof')
        return True
    finally:
        for d in dbs.values(): d.close()
if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--evidence-root',required=True)
    parser.add_argument('--scenario',choices=['normal','barcode-flip'],default='normal')
    args=parser.parse_args()
    try:
        verify(args.evidence_root,args.scenario=='barcode-flip')
        print('A5_INTEGRITY_PASS scenario='+args.scenario)
    except (AssertionError,ValueError,KeyError,sqlite3.Error,OSError) as e:
        print('A5_INTEGRITY_FAIL '+str(e),file=sys.stderr)
        sys.exit(1)
