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

# Independent 11-channel evidence oracle (not the production decision implementation).
# These physical camera / logical channel identities are fixed by the v2.0 topology.
CHANNEL_MAP = {
    'CH01': ('master', 'LOADING', 'HIK-M-1'), 'CH02': ('slave', 'ST03', 'HIK-S-2'),
    'CH03': ('slave', 'ST03', 'HIK-S-1'), 'CH04': ('slave', 'ST04', 'HIK-S-4'),
    'CH05': ('slave', 'ST04', 'HIK-S-3'), 'CH06': ('master', 'ST05', 'HIK-M-2'),
    'CH07': ('master', 'ST06', 'HIK-M-3'), 'CH08': ('slave', 'ST09', 'LINE-S-8'),
    'CH09': ('slave', 'ST09', 'LINE-S-8'), 'CH10': ('master', 'ST10', 'LINE-M-2'),
    'CH11': ('master', 'ST10', 'LINE-M-2')
}
LINE_PAIRS = [('CH08', 'CH09'), ('CH10', 'CH11')]


def verify_eleven_channels(root, hold=False):
    """Read-only, independently verify both Core WALs, both image WALs, every PNG and each ACK.

    The normal fixture may produce only a SIM_ONLY final, never a REAL device pass.
    An independent barcode-flip fixture must produce HOLD with no final write.
    """
    root = pathlib.Path(root).resolve()
    core, vision = {}, {}
    try:
        for role in ('master', 'slave'):
            core_path = root / role / 'production.db'
            vision_path = root / role / 'vision' / 'vision-assets.sqlite'
            check(core_path.is_file() and vision_path.is_file(), f'{role} 11-channel WAL missing')
            for table, path in ((core, core_path), (vision, vision_path)):
                db = sqlite3.connect(path.as_uri() + '?mode=ro', uri=True, timeout=4)
                db.row_factory = sqlite3.Row
                table[role] = db
                check(db.execute('PRAGMA integrity_check').fetchone()[0] == 'ok', f'{role} WAL corrupt')
                check(db.execute('PRAGMA journal_mode').fetchone()[0] == 'wal', f'{role} WAL disabled')

        workpieces, core_results, assets_by_role = {}, {}, {}
        for role in ('master', 'slave'):
            work = rows(core[role], 'SELECT * FROM workpieces')
            check(len(work) == 1, f'{role} expected exactly one physical WorkpieceId')
            workpiece = json.loads(work[0]['identity_json'])
            check(work[0]['workpiece_id'] == workpiece['workpieceId'] and
                  work[0]['epoch'] == workpiece['epoch'] and
                  work[0]['release_id'] == workpiece['releaseId'] and
                  work[0]['barcode'] == workpiece['barcode'], f'{role} immutable workpiece mismatch')
            check(workpiece['barcode'] == 'SIM-0001' and
                  workpiece['releaseId'] == 'SIM_LIGHT_TEST_RELEASE' and
                  workpiece['epoch'] == 1, f'{role} unexpected fixture release')
            workpieces[role] = workpiece

            expected = set(CHANNEL_MAP) if role == 'master' else {
                cid for cid, (owner, _, _) in CHANNEL_MAP.items() if owner == 'slave'
            }
            data = rows(core[role], 'SELECT * FROM channel_results')
            check({r['channel_id'] for r in data} == expected and len(data) == len(expected),
                  f'{role} missing, duplicate or foreign logical channels')
            core_results[role] = {}
            for row in data:
                result = json.loads(row['result_json'])
                channel = result['channelId']
                owner, station, device = CHANNEL_MAP[channel]
                check(result['workpieceId'] == workpiece['workpieceId'] and
                      result['traceId'] == workpiece['traceId'] and
                      result['epoch'] == workpiece['epoch'] and
                      result['releaseId'] == workpiece['releaseId'] and
                      result['stationId'] == station and
                      result['physicalDeviceId'] == device and
                      result['attemptNo'] == 1 and
                      result['isSimulated'] is True and result['outcome'] == 'ok',
                      f'{role}/{channel} lineage or detection mismatch')
                if role == 'slave':
                    check(owner == 'slave', f'{role}/{channel} foreign camera accepted')
                check(row['result_id'] == result['resultId'] and
                      row['workpiece_id'] == result['workpieceId'] and
                      row['channel_id'] == result['channelId'] and
                      row['attempt_no'] == 1, f'{role}/{channel} result WAL index mismatch')
                core_results[role][channel] = result

            asset_rows = rows(vision[role], 'SELECT * FROM vision_assets')
            check(len(asset_rows) == (6 if role == 'master' else 7),
                  f'{role} expected one parent and all native pixel assets')
            image_root = root / role / 'vision'
            assets = {}
            seen_paths = set()
            for a in asset_rows:
                m = json.loads(a['asset_json'])
                id_ = a['frame_id']
                check(id_ == m['frameId'] and a['relative_path'] == m['relativePath'] and
                      a['workpiece_id'] == workpiece['workpieceId'] and
                      a['epoch'] == m['epoch'] == workpiece['epoch'] and
                      a['release_id'] == m['releaseId'] == workpiece['releaseId'] and
                      m['traceId'] == workpiece['traceId'] and m['isSimulation'] is True,
                      f'{role} image WAL metadata mismatch')
                relative = pathlib.Path(m['relativePath'])
                check(not relative.is_absolute() and '..' not in relative.parts, 'Image escaped asset root')
                img = (image_root / relative).resolve()
                check(img.is_relative_to(image_root.resolve()) and img.is_file(),
                      f'{role} missing or escaped PNG {id_}')
                check(img not in seen_paths, f'{role} reused PNG path')
                seen_paths.add(img)
                data = img.read_bytes()
                check(data.startswith(b'\x89PNG\r\n\x1a\n') and
                      len(data) == a['bytes'] and
                      hashlib.sha256(data).hexdigest() == m['contentSha256'],
                      f'{role} corrupted / replaced PNG {id_}')
                assets[id_] = m
            assets_by_role[role] = assets

            for result in core_results[role].values():
                m = assets.get(result['frameId'])
                check(m is not None and
                      m['channelId'] == result['channelId'] and
                      m['captureId'] == result['captureId'] and
                      m['sourceFrameSha256'] == result['sourceFrameSha256'] and
                      m['physicalDeviceId'] == result['physicalDeviceId'] and
                      m['parentFrameId'] == result['parentFrameId'],
                      f"{role}/{result['channelId']} source pixel evidence mismatch")
                found = rows(vision[role], "SELECT result_json FROM vision_results WHERE frame_id='" +
                             result['frameId'] + "'")
                check(len(found) == 1 and json.loads(found[0]['result_json']) == result,
                      f"{role}/{result['channelId']} production / image WAL divergence")
                if m['parentFrameId'] is None:
                    check(m['contentSha256'] == m['sourceFrameSha256'],
                          f"{role}/{result['channelId']} area source digest mismatch")
                else:
                    parent = assets.get(m['parentFrameId'])
                    check(parent is not None and parent['channelId'] is None and
                          parent['parentFrameId'] is None and
                          parent['captureId'] == m['captureId'] and
                          parent['physicalDeviceId'] == m['physicalDeviceId'] and
                          parent['contentSha256'] == result['sourceFrameSha256'] and
                          m['parentRoi'] is not None,
                          f"{role}/{result['channelId']} line-scan parent-child misbinding")
            parent_count = sum(a['channelId'] is None for a in assets.values())
            check(parent_count == 1, f'{role} line parent must be saved exactly once')
            check(len(rows(vision[role], 'SELECT * FROM vision_results')) ==
                  (5 if role == 'master' else 6), f'{role} image result WAL incomplete')

        check(workpieces['master'] == workpieces['slave'], 'Master / Slave WorkpieceId identity mismatch')
        check(all(core_results['master'][x] == core_results['slave'][x]
                  for x in core_results['slave']), 'PeerLink modified committed Slave result')

        for group in LINE_PAIRS:
            owner = CHANNEL_MAP[group[0]][0]
            left, right = (core_results[owner][cid] for cid in group)
            check(left['parentFrameId'] == right['parentFrameId'] and
                  left['captureId'] == right['captureId'] and
                  left['sourceFrameSha256'] == right['sourceFrameSha256'],
                  f'Line camera {group} failed to share one lossless parent')

        for sender, receiver in (('master', 'slave'), ('slave', 'master')):
            outgoing = rows(core[sender], 'SELECT * FROM outbox')
            incoming = rows(core[receiver], 'SELECT * FROM inbox')
            expected_count = 1 if sender == 'master' else 6
            check(len(outgoing) == len(incoming) == expected_count,
                  f'{sender} missing durable PeerLink registrations/results/ACK')
            by_message = {x['message_id']: x for x in incoming}
            check(len(by_message) == len(incoming), f'{receiver} repeated MessageId')
            for msg in outgoing:
                env = json.loads(msg['envelope_json'])
                inbox = by_message.get(msg['message_id'])
                check(inbox is not None and msg['acknowledged_utc'] is not None and
                      env['messageId'] == msg['message_id'] == inbox['message_id'] and
                      env['senderNodeId'] == sender and env['receiverNodeId'] == receiver and
                      env['payloadHash'] == msg['payload_hash'] == inbox['payload_hash'] and
                      hashlib.sha256(env['payloadJson'].encode()).hexdigest() == msg['payload_hash'],
                      f'{sender} false or missing persisted ACK')
                check(hashlib.sha256(msg['envelope_json'].encode()).hexdigest() ==
                      inbox['envelope_hash'], f'{sender} changed envelope identity was ACKed')
                payload = json.loads(env['payloadJson'])
                if sender == 'master':
                    check(env['payloadType'] == 'workpiece_registered' and
                          env['channelId'] is None and payload == workpieces['master'],
                          'Master registered wrong product identity')
                else:
                    channel = env['channelId']
                    check(env['payloadType'] == 'channel_result' and
                          CHANNEL_MAP[channel][0] == 'slave' and
                          payload == core_results['slave'][channel],
                          f'Slave outbox {channel} lacks provenance')

        final = rows(core['master'], 'SELECT final_json FROM workpieces')[0][0]
        check(rows(core['slave'], 'SELECT final_json FROM workpieces')[0][0] is None,
              'Slave may not produce final disposition')
        if hold:
            check(final is None, 'Barcode mismatch must HOLD without final disposition')
            check(not rows(core['master'], "SELECT event_id FROM flight_events WHERE kind='FINAL_DECIDED'"),
                  'Barcode mismatch emitted final decision event')
        else:
            check(final is not None, '11-channel Master final missing')
            d = json.loads(final)
            inspection = set(CHANNEL_MAP) - {'CH01'}
            check(d['disposition'] == 'good' and d['isSimulation'] is True and
                  set(d['expected']) == inspection and set(d['received']) == inspection and
                  'SIM_ONLY' in d['proof'],
                  'Master finalized without ten inspected channels or lost simulation flag')
        return True
    finally:
        for d in list(core.values()) + list(vision.values()):
            d.close()

if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--evidence-root',required=True)
    parser.add_argument('--scenario',choices=['normal','barcode-flip','11channels','11channels-barcode-flip'],default='normal')
    args=parser.parse_args()
    try:
        if args.scenario.startswith('11channels'):
            verify_eleven_channels(args.evidence_root, args.scenario.endswith('barcode-flip'))
        else:
            verify(args.evidence_root,args.scenario=='barcode-flip')
        print('A5_INTEGRITY_PASS scenario='+args.scenario)
    except (AssertionError,ValueError,KeyError,sqlite3.Error,OSError) as e:
        print('A5_INTEGRITY_FAIL '+str(e),file=sys.stderr)
        sys.exit(1)
