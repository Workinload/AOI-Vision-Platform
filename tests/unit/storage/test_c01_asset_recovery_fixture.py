"""Agent03 C-07/C-08 contract-consumer research fixture only; SIM_ONLY.

No production migration, real PLC/camera, native decoder or guaranteed durability.
The DDL below is intentionally isolated from locked v2 reference schema.
"""
import hashlib
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import unittest

SCHEMA = """
CREATE TABLE raw_parent (
 asset_id TEXT PRIMARY KEY, host TEXT NOT NULL, grp TEXT NOT NULL,
 device TEXT NOT NULL, attempt INTEGER NOT NULL CHECK(attempt >= 1),
 expected_sha TEXT NOT NULL, state TEXT NOT NULL
 CHECK(state IN ('PENDING','WRITING','AVAILABLE','CORRUPT','FAILED')),
 uri TEXT, file_sha TEXT, pixel_sha TEXT, byte_count INTEGER,
 UNIQUE(host,grp,device,attempt), UNIQUE(asset_id,grp),
 CHECK(state != 'AVAILABLE' OR (
  uri IS NOT NULL AND length(trim(uri))>0 AND
  file_sha IS NOT NULL AND length(file_sha)=64 AND
  pixel_sha IS NOT NULL AND length(pixel_sha)=64 AND
  byte_count IS NOT NULL AND byte_count>0))
);
CREATE TABLE channel_parent (
 workpiece TEXT NOT NULL, grp TEXT NOT NULL, channel TEXT NOT NULL,
 attempt INTEGER NOT NULL, asset_id TEXT NOT NULL,
 PRIMARY KEY(workpiece,channel,attempt),
 FOREIGN KEY(asset_id,grp) REFERENCES raw_parent(asset_id,grp)
);
CREATE TABLE inbox (
 sender TEXT NOT NULL, message_id TEXT NOT NULL, payload_sha TEXT NOT NULL,
 PRIMARY KEY(sender,message_id)
);
CREATE TABLE effect (
 workpiece TEXT NOT NULL, epoch INTEGER NOT NULL, release_id TEXT NOT NULL,
 channel TEXT NOT NULL, capture_id TEXT NOT NULL, attempt INTEGER NOT NULL,
 PRIMARY KEY(workpiece,epoch,release_id,channel,capture_id,attempt)
);
CREATE TABLE outbox (
 sender TEXT NOT NULL, message_id TEXT NOT NULL, payload_sha TEXT NOT NULL,
 PRIMARY KEY(sender,message_id),
 FOREIGN KEY(sender,message_id) REFERENCES inbox(sender,message_id)
);
"""

def sha(data):
    return hashlib.sha256(data).hexdigest()

def open_db(root, role):
    if role not in ('MASTER', 'SLAVE'):
        raise ValueError('fixture roles only')
    dbpath = Path(root)/role/'events.sqlite'
    dbpath.parent.mkdir(parents=True, exist_ok=True)
    db = sqlite3.connect(dbpath, isolation_level=None)
    db.execute('PRAGMA foreign_keys=ON')
    db.execute('PRAGMA synchronous=FULL')
    if db.execute('PRAGMA journal_mode=WAL').fetchone()[0].lower() != 'wal':
        raise RuntimeError('WAL unavailable')
    db.executescript(SCHEMA)
    return db, dbpath

def safe_path(root, uri):
    if not isinstance(uri,str) or not uri or uri.startswith('./') or '\\' in uri or ':' in uri or '\x00' in uri:
        raise ValueError('noncanonical URI')
    name = Path(uri)
    if name.is_absolute() or '..' in name.parts:
        raise ValueError('URI traversal')
    canonical_root = Path(root).resolve()
    result = (canonical_root/name).resolve()
    if not result.is_relative_to(canonical_root) or result == canonical_root:
        raise ValueError('asset escapes root')
    return result

def parent(db, asset_id, host, group, device, attempt, data):
    db.execute("INSERT INTO raw_parent(asset_id,host,grp,device,attempt,expected_sha,state) VALUES (?,?,?,?,?,?,'PENDING')",
               (asset_id,host,group,device,attempt,sha(data)))

def link(db, workpiece, group, channel, attempt, asset_id):
    db.execute('INSERT INTO channel_parent VALUES (?,?,?,?,?)',
               (workpiece,group,channel,attempt,asset_id))

def publish_raw(db, root, asset_id, uri, payload, abort_after_rename=False):
    target = safe_path(root,uri)
    record = db.execute('SELECT state,expected_sha FROM raw_parent WHERE asset_id=?',
                        (asset_id,)).fetchone()
    if record is None or record[0] != 'PENDING' or sha(payload)!=record[1]:
        raise ValueError('no pending asset or input hash mismatch')
    target.parent.mkdir(parents=True, exist_ok=True)
    if target.exists():
        raise FileExistsError('immutable physical original already exists')
    pending = target.with_suffix(target.suffix+'.tmp')
    with open(pending,'xb') as out:
        out.write(payload)
        out.flush()
        os.fsync(out.fileno())
    os.replace(pending,target)
    if hasattr(os,'O_DIRECTORY'):
        descriptor = os.open(target.parent,os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)
    if abort_after_rename:
        raise RuntimeError('simulated interruption after rename, before DB commit')
    physical = target.read_bytes()
    if not physical or sha(physical)!=sha(payload):
        raise ValueError('on-disk bytes differ')
    db.execute('BEGIN IMMEDIATE')
    try:
        cur=db.execute("UPDATE raw_parent SET state='AVAILABLE',uri=?,file_sha=?,pixel_sha=?,byte_count=? WHERE asset_id=? AND state='PENDING'",
                       (uri,sha(physical),sha(physical),len(physical),asset_id))
        if cur.rowcount!=1:
            raise RuntimeError('CAS failed')
        db.execute('COMMIT')
    except BaseException:
        db.execute('ROLLBACK')
        raise

def verify_or_hold(db, root, asset_id):
    state,uri,stored,px,nbytes=db.execute(
        'SELECT state,uri,file_sha,pixel_sha,byte_count FROM raw_parent WHERE asset_id=?',
        (asset_id,)).fetchone()
    if state!='AVAILABLE':
        return False
    try:
        current=safe_path(root,uri).read_bytes()
        good=bool(current) and len(current)==nbytes and sha(current)==stored==px
    except (OSError,ValueError):
        good=False
    if not good:
        db.execute("UPDATE raw_parent SET state='CORRUPT' WHERE asset_id=? AND state='AVAILABLE'",(asset_id,))
    return good

def accept_message(db, sender, msgid, payload, identity, interrupt=False):
    digest=sha(payload)
    db.execute('BEGIN IMMEDIATE')
    try:
        existing=db.execute('SELECT payload_sha FROM inbox WHERE sender=? AND message_id=?',
                            (sender,msgid)).fetchone()
        if existing:
            if existing[0]!=digest:
                raise ValueError('same MessageId, different payload hash')
            db.execute('COMMIT')
            return 'DUPLICATE_NOOP'
        db.execute('INSERT INTO inbox VALUES (?,?,?)',(sender,msgid,digest))
        db.execute('INSERT INTO effect VALUES (?,?,?,?,?,?)',identity)
        if interrupt:
            raise RuntimeError('injected error before durable outbox insert')
        db.execute('INSERT INTO outbox VALUES (?,?,?)',(sender,msgid,digest))
        db.execute('COMMIT')
        return 'PERSISTED_ACK'
    except BaseException:
        db.execute('ROLLBACK')
        raise

class C01StorageConsumerFixture(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(prefix='aoi-c01-prep-')
        self.addCleanup(self.temp.cleanup)
        self.db,self.dbpath=open_db(self.temp.name,'MASTER')
        self.addCleanup(self.db.close)
        self.asset_root=Path(self.temp.name)/'MASTER'/'images'

    def test_two_host_wal_isolation(self):
        slave,path=open_db(self.temp.name,'SLAVE')
        try:
            self.assertNotEqual(self.dbpath.resolve(),path.resolve())
            self.assertEqual(slave.execute('SELECT count(*) FROM raw_parent').fetchone()[0],0)
            parent(self.db,'r','MASTER','rear','LS-R',1,b'raw')
            self.assertEqual(slave.execute('SELECT count(*) FROM raw_parent').fetchone()[0],0)
        finally:
            slave.close()

    def test_rear_front_two_channel_one_parent_and_recapture(self):
        parent(self.db,'rearA','MASTER','rear-g','LS-R',1,b'R')
        link(self.db,'wp-1','rear-g','CH08',1,'rearA')
        link(self.db,'wp-1','rear-g','CH09',1,'rearA')
        parent(self.db,'frontA','MASTER','front-g','LS-F',1,b'F')
        link(self.db,'wp-1','front-g','CH10',1,'frontA')
        link(self.db,'wp-1','front-g','CH11',1,'frontA')
        with self.assertRaises(sqlite3.IntegrityError):
            parent(self.db,'badDuplicate','MASTER','rear-g','LS-R',1,b'R')
        with self.assertRaises(sqlite3.IntegrityError):
            link(self.db,'wp-2','wrong-group','CH08',1,'rearA')
        parent(self.db,'rearB','MASTER','rear-g','LS-R',2,b'R2')
        self.assertEqual(self.db.execute('SELECT count(*) FROM raw_parent').fetchone()[0],3)
        self.assertEqual(len(set(r[0] for r in self.db.execute('SELECT asset_id FROM channel_parent WHERE channel IN ("CH08","CH09")'))),1)
        self.assertEqual(len(set(r[0] for r in self.db.execute('SELECT asset_id FROM channel_parent WHERE channel IN ("CH10","CH11")'))),1)

    def test_available_rejects_empty_uri_or_hash(self):
        parent(self.db,'r','MASTER','rear','LS-R',1,b'raw')
        for uri,filesha,pixelsha,nbytes in ((None,'a'*64,'b'*64,3),('', 'a'*64,'b'*64,3),('raw/a','', 'b'*64,3),('raw/a','a'*64,None,3),('raw/a','a'*64,'b'*64,0)):
            with self.subTest(uri=uri,filesha=filesha,nbytes=nbytes):
                with self.assertRaises(sqlite3.IntegrityError):
                    self.db.execute("UPDATE raw_parent SET state='AVAILABLE',uri=?,file_sha=?,pixel_sha=?,byte_count=? WHERE asset_id='r'",
                                    (uri,filesha,pixelsha,nbytes))

    def test_actual_bytes_and_tamper_or_missing_hold(self):
        data=bytes(range(256))*4
        parent(self.db,'raw','MASTER','rear','LS-R',1,data)
        publish_raw(self.db,self.asset_root,'raw','raw/rear.bin',data)
        self.assertTrue(verify_or_hold(self.db,self.asset_root,'raw'))
        target=safe_path(self.asset_root,'raw/rear.bin')
        target.write_bytes(b'altered')
        self.assertFalse(verify_or_hold(self.db,self.asset_root,'raw'))
        self.assertEqual(self.db.execute("SELECT state FROM raw_parent WHERE asset_id='raw'").fetchone()[0],'CORRUPT')
        parent(self.db,'raw2','MASTER','front','LS-F',1,b'actual')
        publish_raw(self.db,self.asset_root,'raw2','raw/front.bin',b'actual')
        safe_path(self.asset_root,'raw/front.bin').unlink()
        self.assertFalse(verify_or_hold(self.db,self.asset_root,'raw2'))

    def test_sql_metadata_alone_cannot_prove_available_bytes(self):
        # A raw SQL UPDATE can have a non-hex 64-character digest and missing bytes.
        # Demonstrates why checks and real readback both matter for C-07.
        parent(self.db,'raw','MASTER','rear','LS-R',1,b'expected')
        self.db.execute("UPDATE raw_parent SET state='AVAILABLE',uri=?,file_sha=?,pixel_sha=?,byte_count=1 WHERE asset_id='raw'",
                        ('raw/missing.bin','z'*64,'y'*64))
        self.assertFalse(verify_or_hold(self.db,self.asset_root,'raw'))
        self.assertEqual(self.db.execute('SELECT state FROM raw_parent').fetchone()[0],'CORRUPT')

    def test_lost_raw_asset_prevents_event_and_outbox(self):
        parent(self.db,'raw','MASTER','rear','LS-R',1,b'valid')
        publish_raw(self.db,self.asset_root,'raw','raw/file.bin',b'valid')
        safe_path(self.asset_root,'raw/file.bin').unlink()
        if verify_or_hold(self.db,self.asset_root,'raw'):
            accept_message(self.db,'slave','msg',b'OK',('wp',1,'rel','CH08','cap',1))
        self.assertEqual(self.db.execute('SELECT count(*) FROM effect').fetchone()[0],0)
        self.assertEqual(self.db.execute('SELECT count(*) FROM outbox').fetchone()[0],0)
        self.assertEqual(self.db.execute('SELECT state FROM raw_parent').fetchone()[0],'CORRUPT')

    def test_uri_path_traversal_and_symlink_rejected(self):
        for uri in ('../outside.bin','/etc/passwd','C:\\device.bin','./raw.bin'):
            with self.subTest(uri=uri), self.assertRaises(ValueError):
                safe_path(self.asset_root,uri)
        self.asset_root.mkdir(parents=True)
        (self.asset_root/'escape').symlink_to(Path(self.temp.name).parent, target_is_directory=True)
        with self.assertRaises(ValueError):
            safe_path(self.asset_root,'escape/other.bin')

    def test_abort_after_rename_is_pending_not_false_available(self):
        parent(self.db,'raw','MASTER','rear','LS-R',1,b'staged')
        with self.assertRaises(RuntimeError):
            publish_raw(self.db,self.asset_root,'raw','raw/file.bin',b'staged',abort_after_rename=True)
        self.assertEqual(self.db.execute('SELECT state FROM raw_parent').fetchone()[0],'PENDING')
        self.assertTrue(safe_path(self.asset_root,'raw/file.bin').exists())
        self.assertFalse(verify_or_hold(self.db,self.asset_root,'raw'))

    def test_bad_input_hash_rejects_without_file_write(self):
        parent(self.db,'raw','MASTER','rear','LS-R',1,b'original')
        with self.assertRaises(ValueError):
            publish_raw(self.db,self.asset_root,'raw','raw/file.bin',b'changed')
        self.assertFalse((self.asset_root/'raw/file.bin').exists())

    def test_inbox_outbox_effect_atomic_and_replay_safe(self):
        identity=('wp-a',1,'rel','CH08','cap',1)
        with self.assertRaises(RuntimeError):
            accept_message(self.db,'slave','msg',b'payload',identity,interrupt=True)
        self.assertEqual(self.db.execute('SELECT count(*) FROM inbox').fetchone()[0],0)
        self.assertEqual(self.db.execute('SELECT count(*) FROM outbox').fetchone()[0],0)
        self.assertEqual(self.db.execute('SELECT count(*) FROM effect').fetchone()[0],0)
        self.assertEqual(accept_message(self.db,'slave','msg',b'payload',identity),'PERSISTED_ACK')
        self.assertEqual(accept_message(self.db,'slave','msg',b'payload',identity),'DUPLICATE_NOOP')
        with self.assertRaises(ValueError):
            accept_message(self.db,'slave','msg',b'tampered',identity)
        with self.assertRaises(sqlite3.IntegrityError):
            accept_message(self.db,'slave','different-msg',b'payload',identity)
        self.assertEqual(self.db.execute('SELECT count(*) FROM effect').fetchone()[0],1)
        self.assertEqual(self.db.execute('SELECT count(*) FROM outbox').fetchone()[0],1)

    def test_same_barcode_different_piece_identity_kept(self):
        for wp,msg in (('wp-a','m1'),('wp-b','m2')):
            identity=(wp,1,'rel','CH08','cap',1)
            self.assertEqual(accept_message(self.db,'slave',msg,b'same-barcode',identity),'PERSISTED_ACK')
        self.assertEqual(self.db.execute('SELECT count(*) FROM effect').fetchone()[0],2)

    def test_subprocess_abrupt_exit_before_and_after_commit(self):
        # Child intentionally exits without closing SQLite, only in temp fixture DB.
        child=("import os,sqlite3,sys; db=sqlite3.connect(sys.argv[1],isolation_level=None);"
               "db.execute('PRAGMA journal_mode=WAL');db.execute('BEGIN IMMEDIATE');"
               "db.execute('INSERT INTO inbox VALUES (?,?,?)',(sys.argv[2],sys.argv[2],'a'*64));"
               "db.execute('INSERT INTO effect VALUES (?,?,?,?,?,?)',(sys.argv[2],1,'rel','CH08','cap',1));"
               "db.execute('INSERT INTO outbox VALUES (?,?,?)',(sys.argv[2],sys.argv[2],'a'*64));"
               "db.execute('COMMIT') if sys.argv[3]=='commit' else None;os._exit(int(sys.argv[4]))")
        first=subprocess.run([sys.executable,'-c',child,str(self.dbpath),'before','nocommit','41'],check=False)
        self.assertEqual(first.returncode,41)
        self.assertEqual(self.db.execute("SELECT count(*) FROM inbox WHERE sender='before'").fetchone()[0],0)
        second=subprocess.run([sys.executable,'-c',child,str(self.dbpath),'after','commit','42'],check=False)
        self.assertEqual(second.returncode,42)
        self.assertEqual(self.db.execute("SELECT count(*) FROM inbox WHERE sender='after'").fetchone()[0],1)
        self.assertEqual(self.db.execute("SELECT count(*) FROM outbox WHERE sender='after'").fetchone()[0],1)
        print('INJECTED_CHILD_EXIT_CODES=41,42 (EXPECTED); AFTER_COMMIT_PERSISTED=1')

    def test_online_sqlite_backup_includes_wal_committed_rows(self):
        accept_message(self.db,'slave','msg',b'payload',('wp',1,'rel','CH08','cap',1))
        snapshot=Path(self.temp.name)/'copy.sqlite'
        with sqlite3.connect(snapshot) as backup:
            self.db.backup(backup)
            self.assertEqual(backup.execute('SELECT count(*) FROM inbox').fetchone()[0],1)
        self.assertTrue(snapshot.exists())

if __name__=='__main__':
    unittest.main()
