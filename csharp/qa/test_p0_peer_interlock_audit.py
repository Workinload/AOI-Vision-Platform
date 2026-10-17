"""Independent A5 peer-break audit unit fixtures (NOT five-process evidence)."""
from contextlib import closing
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
import uuid

from p0_fins_timeline_audit import AuditFailure
from p0_peer_interlock_audit import check_evidence
from test_p0_fins_timeline_audit import sample_packet


def connect_schema(path, ddl, rows):
    path.parent.mkdir(parents=True, exist_ok=True)
    with closing(sqlite3.connect(path)) as con, con:
        con.execute(ddl)
        for statement, params in rows:
            con.execute(statement, params)


class PeerInterlockEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="a5-peer-audit-")
        self.root = Path(self.temp.name)
        self.identity = str(uuid.uuid4())
        for role in ("master", "slave"):
            connect_schema(self.root / role / "host-peer-interlock.sqlite",
                           "CREATE TABLE host_peer_interlock(singleton INTEGER,latched_utc TEXT,reason_code TEXT,detail TEXT)",
                           [("INSERT INTO host_peer_interlock VALUES(1,?,?,?)",
                             ("2026-10-17T08:00:01+00:00", "PEER_LINK_LOST", "real TCP EOF"))])
            connect_schema(self.root / role / "production.db",
                           "CREATE TABLE workpieces(workpiece_id TEXT, final_json TEXT)",
                           [("INSERT INTO workpieces VALUES(?,NULL)", (self.identity,))])
            (self.root / f"{role}.log").write_text(
                f"SIM_FULL_{role.upper()}_PEER_INTERLOCK_HOLD\n"
                f"SIM_FULL_{role.upper()}_PEER_READONLY_RECONNECTED\n", encoding="utf8")
        connect_schema(self.root / "master/sim-full-cycle.db",
                       "CREATE TABLE sim_full_tokens(token INT,phase TEXT)",
                       [("INSERT INTO sim_full_tokens VALUES(1,'registered')", ())])
        self.log = self.root / "a0-real-five-process.log"
        self.log.write_text("".join(
            f"A0_PEER_LOSS_STARTED name={name} pid={100+i}\n"
            for i, name in enumerate(("vplc", "master", "slave", "master-ui", "slave-ui"))) +
            "A0_PEER_LOSS_INTERLOCK_E2E_PASS process_restart_blocked=1 real_hardware_writes=0\n",
            encoding="utf8")
        self.wire = []
        self.add_wire(4302, True, (1,), ())
        self.add_wire(1999, False, (), (1,))
        self.save_wire()

    def tearDown(self):
        self.temp.cleanup()

    def add_wire(self, address, write, words, response_words):
        sid = len(self.wire) + 1
        request, response = sample_packet(write, address, words, sid, response_words)
        self.wire.append(dict(Sequence=sid, ReceivedUtc="2026-10-17T08:00:00+00:00",
                              MonotonicTimestamp=sid * 100, Sid=sid, Token=1,
                              IsWrite=write, Address=address, Values=list(words),
                              Outcome="OK", RequestHex=request.hex(),
                              ResponseHex=response.hex()))

    def save_wire(self):
        (self.root / "p0-fins-wire.jsonl").write_text(
            "".join(json.dumps(x) + "\n" for x in self.wire), encoding="utf8")

    def verify(self):
        return check_evidence(self.root, self.log)

    def rejects(self, code):
        with self.assertRaises(AuditFailure) as a:
            self.verify()
        self.assertEqual(a.exception.code, code)

    def test_read_only_peerbreak_wal_and_raw_fins_fixture(self):
        result = self.verify()
        self.assertTrue(result["no_automatic_PLC_replay"])
        self.assertEqual(result["real_fins_packets_checked"], 2)

    def test_real_raw_fins_final_packet_hidden_after_reconnect_fails(self):
        self.add_wire(1016, True, (1,), ())
        self.save_wire()
        self.rejects("UNSAFE_REPLAY_FINS_WRITE")

    def test_forged_fins_json_address_cannot_hide_raw_d2099_write(self):
        self.add_wire(2099, True, (2,), ())
        self.wire[-1]["Address"] = 4302
        self.save_wire()
        self.rejects("FINS_METADATA_MISMATCH")

    def test_missing_real_restart_probe_must_remain_unverified(self):
        self.log.write_text("A0_PEER_LOSS_STARTED name=master pid=100\n", encoding="utf8")
        self.rejects("NOT_VERIFIED")

    def test_missing_slave_persistent_latch_is_not_safe_peer_recovery(self):
        with closing(sqlite3.connect(self.root / "slave/host-peer-interlock.sqlite")) as c, c:
            c.execute("DELETE FROM host_peer_interlock")
        self.rejects("WAL_STATE_INCONSISTENT")

    def test_final_decision_after_disconnection_is_disallowed(self):
        with closing(sqlite3.connect(self.root / "master/production.db")) as c, c:
            c.execute("UPDATE workpieces SET final_json='{\"disposition\":\"good\"}'")
        self.rejects("FALSE_FINAL_AFTER_DISCONNECT")


if __name__ == "__main__":
    unittest.main()
