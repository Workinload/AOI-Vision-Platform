"""PREP-only C11/C12 isolated negative contracts, NOT Core/IPC or frozen C01."""
import unittest
from dataclasses import dataclass

@dataclass(frozen=True)
class Cursor:
    host: str
    stream: str
    epoch: int
    seq: int
    digest: str

class ReadProjection:
    def __init__(self):
        self.online = False
        self.cursor = None
        self.signals = {}
        self.selected = None

    def connect(self):
        self.online = False
        self.cursor = None
        self.signals.clear()
        self.selected = None

    disconnect = connect

    def full(self, cursor, signals):
        if not cursor.host or not cursor.stream or cursor.seq < 0 or not cursor.digest:
            self.connect()
            return False
        if len(signals) != len(set(signals)):
            self.connect()
            return False
        self.cursor = cursor
        self.signals = dict(signals)
        self.online = True
        return True

    def delta(self, cursor, update):
        old = self.cursor
        if not self.online or old is None:
            return False
        if (cursor.host, cursor.stream, cursor.epoch) != (old.host, old.stream, old.epoch):
            self.connect()
            return False
        if cursor.seq == old.seq and cursor.digest == old.digest:
            return True
        if cursor.seq != old.seq + 1 or any(k not in self.signals for k in update):
            self.connect()
            return False
        self.signals.update(update)
        self.cursor = cursor
        return True

    def choose(self, entity):
        if not self.online or entity not in self.signals:
            return False
        self.selected = entity
        return True

@dataclass(frozen=True)
class Session:
    role: str
    valid: bool
    expiry: int
    revoked: bool = False

def authorize(session, command, now):
    if not session or not session.valid or session.revoked or now >= session.expiry:
        return False
    if session.role not in ('VIEWER', 'OPERATOR', 'ENGINEER', 'ADMIN'):
        return False
    return command == 'read'  # ALL mutations, even admin, denied until C01 freeze

class Alarm:
    def __init__(self):
        self.active = True
        self.acknowledged = False
        self.cleared = False
    def ack(self, authorized):
        if not authorized: return False
        self.acknowledged = True
        return True
    def clear(self, authorized):
        if not authorized or self.active or not self.acknowledged: return False
        self.cleared = True
        return True

class C01ConsumerNegativePrep(unittest.TestCase):
    def setUp(self):
        self.r = ReadProjection()
        self.start = Cursor('MOCK:MASTER','MOCK:STREAM',1,1,'digest1')
        self.signals = {'MOCK:DEV01': 'UNKNOWN','MOCK:DEV02': 'UNKNOWN'}

    def test_initial_offline(self):
        self.assertFalse(self.r.online)
        self.assertFalse(self.r.choose('MOCK:DEV01'))
        self.assertFalse(self.r.delta(self.start, {}))

    def test_full_then_delta(self):
        self.assertTrue(self.r.full(self.start,self.signals))
        self.assertTrue(self.r.delta(Cursor('MOCK:MASTER','MOCK:STREAM',1,2,'digest2'),{'MOCK:DEV01':'OFFLINE'}))
        self.assertTrue(self.r.choose('MOCK:DEV01'))

    def test_disconnect_drops_stale(self):
        self.r.full(self.start,self.signals);self.r.disconnect()
        self.assertFalse(self.r.online);self.assertEqual(self.r.signals,{})
        self.assertFalse(self.r.delta(Cursor('MOCK:MASTER','MOCK:STREAM',1,2,'digest2'),{}))

    def test_cursor_gap_fail_closed(self):
        self.r.full(self.start,self.signals)
        self.assertFalse(self.r.delta(Cursor('MOCK:MASTER','MOCK:STREAM',1,3,'digest3'),{}))
        self.assertFalse(self.r.online)

    def test_cursor_conflicting_replay(self):
        self.r.full(self.start,self.signals)
        self.assertFalse(self.r.delta(Cursor('MOCK:MASTER','MOCK:STREAM',1,1,'DIFFERENT'),{}))
        self.assertFalse(self.r.online)

    def test_cursor_identical_replay(self):
        self.r.full(self.start,self.signals)
        self.assertTrue(self.r.delta(self.start,{}))

    def test_foreign_host_and_epoch(self):
        for c in (Cursor('MOCK:SLAVE','MOCK:STREAM',1,2,'d2'),Cursor('MOCK:MASTER','MOCK:STREAM',2,2,'d2')):
            self.r.full(self.start,self.signals)
            self.assertFalse(self.r.delta(c,{}))
            self.assertFalse(self.r.online)

    def test_foreign_entity_rejected(self):
        self.r.full(self.start,self.signals)
        self.assertFalse(self.r.delta(Cursor('MOCK:MASTER','MOCK:STREAM',1,2,'d2'),{'MOCK:UNKNOWN':'OK'}))

    def test_shared_entity_selection(self):
        self.r.full(self.start,self.signals)
        self.assertTrue(self.r.choose('MOCK:DEV02'))
        self.assertIn(self.r.selected,self.r.signals)

    def test_no_anonymous_or_expired(self):
        self.assertFalse(authorize(None,'read',100))
        self.assertFalse(authorize(Session('ADMIN',True,100),'read',100))
        self.assertFalse(authorize(Session('ADMIN',True,200,True),'read',100))

    def test_unknown_role_or_unknown_command(self):
        self.assertFalse(authorize(Session('ROOT',True,200),'read',100))
        self.assertFalse(authorize(Session('ADMIN',True,200),'unknown_opcode',100))

    def test_admin_production_write_denied(self):
        for command in ('physical_plc_write','publish_solution','set_parameter','alarm_ack','alarm_clear'):
            self.assertFalse(authorize(Session('ADMIN',True,200),command,100))

    def test_ack_is_not_clear(self):
        alarm=Alarm()
        self.assertTrue(alarm.ack(True))
        self.assertTrue(alarm.active)
        self.assertFalse(alarm.cleared)
        self.assertFalse(alarm.clear(True))

    def test_clear_requires_restore_and_ack(self):
        alarm=Alarm();alarm.active=False
        self.assertFalse(alarm.clear(True))
        alarm.ack(True)
        self.assertFalse(alarm.clear(False))
        self.assertTrue(alarm.clear(True))

if __name__=='__main__':
    unittest.main(verbosity=2)
