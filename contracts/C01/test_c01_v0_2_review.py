#!/usr/bin/env python3
"""C01 v0.2 review-only executable schema and mocked invariants; NOT W004 acceptance.
Requires jsonschema>=4.18. Does not connect to any PLC or camera.
"""
import copy,hashlib,json,sqlite3,tempfile,unittest
from pathlib import Path
from jsonschema import Draft202012Validator,FormatChecker

HERE=Path(__file__).resolve().parent
SCHEMA=json.loads((HERE/'C01_EVENT_VARIANTS_REVIEW_V0_2.schema.json').read_text())
VALIDATOR=Draft202012Validator(SCHEMA,format_checker=FormatChecker())
SHA='a'*64

def sample(kind):
    data={
     'WORKPIECE_RESULT':dict(WorkpieceId='WP-001',ReleaseId='R-001',ChannelId='CH08',CaptureId='C-001',AttemptNo=1,PhysicalDeviceId='CAM-L-1',CaptureGroupId='CG-001',ProcessingStatus='SUCCEEDED',QualityStatus='NG'),
     'ALARM_TRANSITION':dict(AlarmId='AL-1',EntityId='ST-1',Transition='RAISE',ReasonCode='SENSOR_FAIL'),
     'DEPLOYMENT':dict(DeploymentId='DP-1',ReleaseId='R-001',Phase='STAGED'),
     'ASSET':dict(AssetId='AS-1',FrameId='FR-001',CaptureGroupId='CG-001',PhysicalDeviceId='CAM-L-1',AttemptNo=1,ChannelIds=['CH08','CH09'],State='PENDING'),
     'UI_SNAPSHOT':dict(SnapshotId='SN-1',SourceHost='MASTER',Cursor=0,Digest=SHA,Connection='FRESH',IsLive=True,EntityVersion=1),
     'AUTH_AUDIT':dict(SubjectId='usr-1',Action='READ',EntityId='ST-1',Decision='DENY',ReasonCode='NO_PERMISSION',AuditHash=SHA),
    }[kind]
    return dict(SchemaVersion='C01-REVIEW-0.2',MessageId='MSG-1',PayloadHash=SHA,EventId='EV-1',SequenceNo=1,OccurredUtc='2026-10-16T00:00:00Z',HostId='MASTER',Epoch=2,TraceId='TR-1',EventKind=kind,Data=data)

def allowed(msg):
    return not list(VALIDATOR.iter_errors(msg))

def readback_verdict(message,current_epoch):
    """Mock runtime check impossible to guarantee solely with a JSON schema."""
    if not allowed(message) or message['EventKind']!='DEPLOYMENT':return False
    if message['Epoch']!=current_epoch:return False
    data=message['Data']
    if data['Phase']!='PREPARED_BOTH':return False
    receipts=data['CoreReadbacks']
    if {r['HostId'] for r in receipts}!={'MASTER','SLAVE'}:return False
    if len({r['StagedReleaseHash'] for r in receipts})!=1:return False
    if len({r['DeviceReadbackHash'] for r in receipts})!=1:return False
    if data['VplcReadback']['ParametersHash']!=receipts[0]['DeviceReadbackHash']:return False
    return True

class SchemaCases(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        Draft202012Validator.check_schema(SCHEMA)

    def reject(self,msg):
        self.assertFalse(allowed(msg),str(msg))

    def test_all_six_variants_accepted(self):
        for kind in ('WORKPIECE_RESULT','ALARM_TRANSITION','DEPLOYMENT','ASSET','UI_SNAPSHOT','AUTH_AUDIT'):
            with self.subTest(kind=kind):self.assertTrue(allowed(sample(kind)))
    def test_non_workpiece_events_do_not_require_channel_or_capture(self):
        for kind in ('ALARM_TRANSITION','DEPLOYMENT','ASSET','UI_SNAPSHOT','AUTH_AUDIT'):
            with self.subTest(kind=kind):
                p=sample(kind)
                self.assertNotIn('WorkpieceId',p['Data'])
                self.assertTrue(allowed(p))
    def test_common_unknown_field_denied(self):
        p=sample('WORKPIECE_RESULT');p['ProducerInjectedAdmin']=True;self.reject(p)
    def test_wrong_schema_version_denied(self):
        p=sample('WORKPIECE_RESULT');p['SchemaVersion']='C01-DRAFT-0';self.reject(p)
    def test_bool_epoch_denied(self):
        p=sample('WORKPIECE_RESULT');p['Epoch']=True;self.reject(p)
    def test_epoch_zero_denied(self):
        p=sample('WORKPIECE_RESULT');p['Epoch']=0;self.reject(p)
    def test_bad_sha_denied(self):
        p=sample('ASSET');p['PayloadHash']='X'*64;self.reject(p)
    def test_bad_timestamp_denied(self):
        p=sample('ASSET');p['OccurredUtc']='this is not UTC';self.reject(p)
    def test_workpiece_error_never_ok(self):
        for st in ('PENDING','RUNNING','ERROR','CANCELLED','TIMEOUT'):
            with self.subTest(status=st):
                p=sample('WORKPIECE_RESULT');p['Data'].update(ProcessingStatus=st,QualityStatus='OK');self.reject(p)
    def test_successful_processing_can_be_ng(self):
        self.assertTrue(allowed(sample('WORKPIECE_RESULT')))
    def test_attempt_zero_denied(self):
        p=sample('WORKPIECE_RESULT');p['Data']['AttemptNo']=0;self.reject(p)
    def test_channel_12_denied(self):
        p=sample('WORKPIECE_RESULT');p['Data']['ChannelId']='CH12';self.reject(p)
    def test_alarm_ack_not_clear(self):
        p=sample('ALARM_TRANSITION');p['Data']['Transition']='ACK';self.reject(p)
        p['Data'].update(ActorId='A-1',AcknowledgementId='ACK-1');self.assertTrue(allowed(p))
        p['Data']['Transition']='CLEAR';self.reject(p)
        p['Data']['ClearEvidenceId']='CLEAR-1';self.assertTrue(allowed(p))
    def test_deployment_prepared_requires_receipts(self):
        p=sample('DEPLOYMENT');p['Data']['Phase']='PREPARED_BOTH';self.reject(p)
    def test_deployment_duplicate_core_denied(self):
        p=prepared();p['Data']['CoreReadbacks'][1]['HostId']='MASTER';self.reject(p)
    def test_prepared_both_needs_virtual_plc(self):
        p=prepared();del p['Data']['VplcReadback'];self.reject(p)
    def test_prepared_both_needs_matched_receipts(self):
        p=prepared();p['Data']['CoreReadbacks'][0]['ReadBackMatched']=False;self.reject(p)
    def test_prepared_both_runtime_epoch_guard(self):
        self.assertFalse(readback_verdict(prepared(),current_epoch=3))
        self.assertTrue(readback_verdict(prepared(),current_epoch=2))
    def test_prepared_both_runtime_hash_guard(self):
        p=prepared();p['Data']['VplcReadback']['ParametersHash']='b'*64
        self.assertTrue(allowed(p)) # Schema cannot express runtime cross-field equality.
        self.assertFalse(readback_verdict(p,current_epoch=2))
    def test_asset_available_needs_complete_metadata(self):
        p=sample('ASSET');p['Data']['State']='AVAILABLE';self.reject(p)
        p['Data'].update(StorageUri='assets/site/raw-1.png',EncodedSha256=SHA,DecodedPixelSha256=SHA,SizeBytes=1024)
        self.assertTrue(allowed(p))
    def test_asset_subdirectory_traversal_denied(self):
        for uri in ('assets/../steal.bin','assets/./safe.png','assets//double.png'):
            with self.subTest(uri=uri):
                p=sample('ASSET')
                p['Data'].update(State='AVAILABLE',StorageUri=uri,EncodedSha256=SHA,DecodedPixelSha256=SHA,SizeBytes=1)
                self.reject(p)
    def test_asset_traversal_and_empty_hash_denied(self):
        p=sample('ASSET');p['Data'].update(State='AVAILABLE',StorageUri='../secrets',EncodedSha256=SHA,DecodedPixelSha256=SHA,SizeBytes=100);self.reject(p)
        p['Data']['StorageUri']='assets/ok.png';p['Data']['DecodedPixelSha256']='';self.reject(p)
    def test_ui_disconnected_never_live(self):
        p=sample('UI_SNAPSHOT');p['Data']['Connection']='DISCONNECTED';self.reject(p)
        p['Data']['IsLive']=False;self.assertTrue(allowed(p))
    def test_auth_event_no_implicit_permission(self):
        p=sample('AUTH_AUDIT');p['Data']['Action']='PLC_WRITE';p['Data']['Decision']='DENY';self.assertTrue(allowed(p))
        # A client sending ALLOW is not proof of server authorization.
        p['Data']['Decision']='ALLOW';self.assertTrue(allowed(p))
        self.assertFalse(default_deny_write(role='ADMIN',action='PLC_WRITE',sim_only=True))
        self.assertFalse(default_deny_write(role='UNKNOWN',action='CONFIG_EDIT',sim_only=True))

def prepared():
    p=sample('DEPLOYMENT');p['Data']['Phase']='PREPARED_BOTH'
    p['Data']['CoreReadbacks']=[dict(HostId=host,StagedReleaseHash=SHA,DeviceReadbackHash=SHA,ReadBackMatched=True) for host in ('MASTER','SLAVE')]
    p['Data']['VplcReadback']=dict(ParametersHash=SHA,ReadBackMatched=True,Simulated=True)
    return p

def default_deny_write(role,action,sim_only):
    if sim_only and action=='PLC_WRITE':return False
    return role in {'ENGINEER','ADMIN'} and action=='CONFIG_EDIT'

class PersistenceOracles(unittest.TestCase):
    def setUp(self):
        self.db=sqlite3.connect(':memory:',isolation_level=None)
        self.db.executescript('''CREATE TABLE raw_frame (
           id INTEGER PRIMARY KEY, host TEXT NOT NULL, capture_group TEXT NOT NULL,
           physical_device TEXT NOT NULL, attempt INTEGER NOT NULL CHECK(attempt>0),
           UNIQUE(host,capture_group,physical_device,attempt));
         CREATE TABLE channel_asset(channel TEXT NOT NULL,raw_frame_id INTEGER NOT NULL,
           FOREIGN KEY(raw_frame_id) REFERENCES raw_frame(id));
         CREATE TABLE outbox(message_id TEXT PRIMARY KEY,payload_hash TEXT NOT NULL);''')
    def tearDown(self):
        self.db.close()
    def test_shared_linescan_parent_once(self):
        self.db.execute("INSERT INTO raw_frame(host,capture_group,physical_device,attempt) VALUES('SLAVE','G1','L1',1)")
        fid=self.db.execute('SELECT id FROM raw_frame').fetchone()[0]
        for ch in ('CH08','CH09'):
            self.db.execute('INSERT INTO channel_asset(channel,raw_frame_id) VALUES(?,?)',(ch,fid))
        self.assertEqual(len(self.db.execute('SELECT raw_frame_id FROM channel_asset').fetchall()),2)
        self.assertEqual(self.db.execute('SELECT count(*) FROM raw_frame').fetchone()[0],1)
        with self.assertRaises(sqlite3.IntegrityError):self.db.execute("INSERT INTO raw_frame(host,capture_group,physical_device,attempt) VALUES('SLAVE','G1','L1',1)")
    def test_new_attempt_not_same_parent(self):
        for attempt in (1,2):self.db.execute('INSERT INTO raw_frame(host,capture_group,physical_device,attempt) VALUES(?,?,?,?)',('SLAVE','G1','L1',attempt))
        self.assertEqual(self.db.execute('SELECT count(*) FROM raw_frame').fetchone()[0],2)
    def test_wal_transaction_rolls_back_frame_and_outbox(self):
        self.db.execute('BEGIN IMMEDIATE')
        self.db.execute("INSERT INTO raw_frame(host,capture_group,physical_device,attempt) VALUES('SLAVE','G1','L1',1)")
        self.db.execute("INSERT INTO outbox(message_id,payload_hash) VALUES('M1',?)",(SHA,))
        self.db.execute('ROLLBACK')
        self.assertEqual(self.db.execute('SELECT count(*) FROM raw_frame').fetchone()[0],0)
        self.assertEqual(self.db.execute('SELECT count(*) FROM outbox').fetchone()[0],0)
    def test_outbox_duplicate_id_rejected(self):
        self.db.execute("INSERT INTO outbox(message_id,payload_hash) VALUES('M1',?)",(SHA,))
        with self.assertRaises(sqlite3.IntegrityError):self.db.execute("INSERT INTO outbox(message_id,payload_hash) VALUES('M1',?)",('b'*64,))
    def test_actual_file_bytes_not_proved_by_uri(self):
        with tempfile.TemporaryDirectory() as tmp:
            f=Path(tmp)/'a.png';f.write_bytes(b'safe_pixels')
            h=hashlib.sha256(f.read_bytes()).hexdigest()
            self.assertTrue(h)
            f.write_bytes(b'tampered_pixels')
            self.assertNotEqual(h,hashlib.sha256(f.read_bytes()).hexdigest())
            # A syntactically valid metadata payload never proves bytes exist.
            p=sample('ASSET');p['Data'].update(State='AVAILABLE',StorageUri='assets/a.png',EncodedSha256=h,DecodedPixelSha256=SHA,SizeBytes=11)
            self.assertTrue(allowed(p))

if __name__=='__main__':
    unittest.main(verbosity=2)
