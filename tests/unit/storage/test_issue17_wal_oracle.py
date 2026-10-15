"""Issue #17: isolated SQLite WAL and asset negative tests. Fixture only; SIM_ONLY."""
import hashlib
from pathlib import Path
import sqlite3
import tempfile
import unittest

DDL = """
CREATE TABLE raw_parent(
 host TEXT NOT NULL, capture_group TEXT NOT NULL, device TEXT NOT NULL,
 sha TEXT NOT NULL, uri TEXT, state TEXT NOT NULL,
 UNIQUE(host,capture_group,device),
 CHECK(state <> 'AVAILABLE' OR (uri IS NOT NULL AND length(trim(uri)) > 0 AND length(sha)=64))
);
CREATE TABLE event_effect(
 sender TEXT NOT NULL, message TEXT NOT NULL, payload_hash TEXT NOT NULL,
 PRIMARY KEY(sender,message)
);
CREATE TABLE outbox(
 sender TEXT NOT NULL, message TEXT NOT NULL,
 PRIMARY KEY(sender,message),
 FOREIGN KEY(sender,message) REFERENCES event_effect(sender,message)
);
"""

def fixture_db(root, role):
    if role not in ('MASTER', 'SLAVE'):
        raise ValueError('SIM_ONLY hosts only')
    path = Path(root) / role / 'local.sqlite'
    path.parent.mkdir(parents=True, exist_ok=True)
    db = sqlite3.connect(path, isolation_level=None)
    db.execute('PRAGMA foreign_keys=ON')
    assert db.execute('PRAGMA journal_mode=WAL').fetchone()[0].lower() == 'wal'
    db.execute('PRAGMA synchronous=FULL')
    db.executescript(DDL)
    return db, path

def insert_event_and_outbox(db, sender, message, payload, fail=False):
    sha = hashlib.sha256(payload).hexdigest()
    db.execute('BEGIN IMMEDIATE')
    try:
        old = db.execute('SELECT payload_hash FROM event_effect WHERE sender=? AND message=?',
                         (sender,message)).fetchone()
        if old is not None:
            if old[0] != sha:
                raise ValueError('message identity collision')
            db.execute('COMMIT')
            return False
        db.execute('INSERT INTO event_effect VALUES (?,?,?)',(sender,message,sha))
        if fail:
            raise RuntimeError('simulated failure between event and outbox')
        db.execute('INSERT INTO outbox VALUES (?,?)',(sender,message))
        db.execute('COMMIT')
        return True
    except BaseException:
        db.execute('ROLLBACK')
        raise

class SimOnlySqliteOracle(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix='aoi-issue17-')
        self.addCleanup(self.tmp.cleanup)
        self.master, self.master_path = fixture_db(self.tmp.name, 'MASTER')
        self.slave, self.slave_path = fixture_db(self.tmp.name, 'SLAVE')
        self.addCleanup(self.master.close)
        self.addCleanup(self.slave.close)

    def test_distinct_master_slave_wal(self):
        self.assertNotEqual(self.master_path.resolve(), self.slave_path.resolve())
        self.assertEqual(self.master.execute('PRAGMA journal_mode').fetchone()[0], 'wal')
        self.assertEqual(self.slave.execute('PRAGMA journal_mode').fetchone()[0], 'wal')
        insert_event_and_outbox(self.master,'slave','msg1',b'NG')
        self.assertEqual(self.master.execute('SELECT count(*) FROM event_effect').fetchone()[0],1)
        self.assertEqual(self.slave.execute('SELECT count(*) FROM event_effect').fetchone()[0],0)

    def test_wal_snapshot_isolation(self):
        observer = sqlite3.connect(self.master_path,isolation_level=None)
        self.addCleanup(observer.close)
        observer.execute('BEGIN')
        self.assertEqual(observer.execute('SELECT count(*) FROM event_effect').fetchone()[0],0)
        insert_event_and_outbox(self.master,'slave','msg1',b'NG')
        self.assertEqual(observer.execute('SELECT count(*) FROM event_effect').fetchone()[0],0)
        observer.execute('COMMIT')
        self.assertEqual(observer.execute('SELECT count(*) FROM event_effect').fetchone()[0],1)

    def test_one_raw_parent_for_two_channel_refs(self):
        sha = hashlib.sha256(b'raw').hexdigest()
        self.master.execute("INSERT INTO raw_parent VALUES (?,?,?,?,?,?)",
                            ('MASTER','group','linescan-rear',sha,'raw/frame.raw','AVAILABLE'))
        with self.assertRaises(sqlite3.IntegrityError):
            self.master.execute("INSERT INTO raw_parent VALUES (?,?,?,?,?,?)",
                                ('MASTER','group','linescan-rear',sha,'raw/duplicate.raw','AVAILABLE'))
        self.assertEqual(self.master.execute('SELECT count(*) FROM raw_parent').fetchone()[0],1)

    def test_available_requires_uri_and_hash(self):
        valid = hashlib.sha256(b'raw').hexdigest()
        for idx, (sha,uri) in enumerate(((valid,None),(valid,''),('bad','raw/x'))):
            with self.subTest(idx=idx), self.assertRaises(sqlite3.IntegrityError):
                self.master.execute('INSERT INTO raw_parent VALUES (?,?,?,?,?,?)',
                                    ('MASTER',str(idx),'camera',sha,uri,'AVAILABLE'))

    def test_event_replay_deduplicates_and_conflict_denied(self):
        self.assertTrue(insert_event_and_outbox(self.master,'s','m',b'original'))
        self.assertFalse(insert_event_and_outbox(self.master,'s','m',b'original'))
        with self.assertRaises(ValueError):
            insert_event_and_outbox(self.master,'s','m',b'changed')
        self.assertEqual(self.master.execute('SELECT count(*) FROM outbox').fetchone()[0],1)

    def test_injected_transaction_failure_rolls_back(self):
        with self.assertRaises(RuntimeError):
            insert_event_and_outbox(self.master,'s','m',b'data',fail=True)
        self.assertEqual(self.master.execute('SELECT count(*) FROM event_effect').fetchone()[0],0)
        self.assertEqual(self.master.execute('SELECT count(*) FROM outbox').fetchone()[0],0)

    def test_test_only_ddl_failure_does_not_commit(self):
        self.master.execute('BEGIN IMMEDIATE')
        try:
            self.master.execute('CREATE TABLE future_only(i INTEGER)')
            with self.assertRaises(sqlite3.OperationalError):
                self.master.execute('CREAT TABLE broken(i INTEGER)')
        finally:
            self.master.execute('ROLLBACK')
        self.assertEqual(self.master.execute("SELECT count(*) FROM sqlite_master WHERE name='future_only'").fetchone()[0],0)

if __name__ == '__main__':
    unittest.main()
