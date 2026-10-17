"""Unit checks for Agent05 terminal auditor; all files below are TEST FIXTURES.

Real five-process acceptance is ONLY p0_terminal_wire_audit.py invoked against
actual Host data. These unit fixtures specifically prove that a superficially
correct final DM mirror cannot conceal wrong SID, duplicate write, missing
first readback, missing durable HOLD, or late post-output HOLD.
"""
from contextlib import closing
import datetime as dt
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
import uuid

from p0_fins_timeline_audit import AuditFailure
from p0_terminal_wire_audit import verify_same_run
from test_p0_fins_timeline_audit import sample_packet


def encoded_words(value):
    data = value.encode("ascii").ljust(20, b"\0")
    return tuple(int.from_bytes(data[i:i + 2], "big") for i in range(0, 20, 2))


def prepare(root: Path, scenario="BARCODE_MISMATCH"):
    (root / "master").mkdir(exist_ok=True, parents=True)
    ledger = root / "master/sim-full-cycle.db"
    master = root / "master/production.db"
    items = [(token, str(uuid.uuid4())) for token in (1, 2, 3)]
    with closing(sqlite3.connect(ledger)) as c, c:
        c.execute("CREATE TABLE sim_full_tokens (token INT, identity_json TEXT, phase TEXT)")
        for token, identity in items:
            c.execute("INSERT INTO sim_full_tokens VALUES (?,?,?)",
                      (token, json.dumps(dict(workpieceId=identity,
                                               epoch=1, releaseId="SIM_REL", barcode=f"SIM-{token:04d}")),
                       "exited" if token < 3 else "registered"))
    with closing(sqlite3.connect(master)) as c, c:
        c.execute("CREATE TABLE workpieces (workpiece_id TEXT, final_json TEXT)")
        c.execute("CREATE TABLE workpiece_holds (workpiece_id TEXT, reason_code TEXT,detail TEXT,raised_utc TEXT)")
        for token, identity in items:
            c.execute("INSERT INTO workpieces VALUES (?,?)",
                      (identity, '{"disposition":"reject"}' if token < 3 else None))
        reason = "END_BARCODE_MISMATCH" if scenario == "BARCODE_MISMATCH" else "END_BARCODE_SCAN_FAILED"
        c.execute("INSERT INTO workpiece_holds VALUES (?,?,?,?)",
                  (items[-1][1], reason, "manual reconciliation required",
                   "2026-10-17T08:00:00+00:00"))
    rows = []
    def add(token, address, write=False, words=(), reply=(), outcome="OK"):
        sequence = len(rows) + 1
        request, response = sample_packet(write, address, words, sequence, reply)
        row = dict(Sequence=sequence, ReceivedUtc="2026-10-17T08:00:00+00:00",
                   MonotonicTimestamp=sequence * 100,
                   Sid=sequence, Token=token, IsWrite=write, Address=address,
                   Values=list(words), Outcome=outcome, RequestHex=request.hex(),
                   ResponseHex=response.hex())
        rows.append(row)
        return row

    for token in (1, 2):
        add(token, 4303, write=True, words=(token, 0))
        add(token, 2099, reply=(1,))
        add(token, 1016, write=True, words=(2,))
        add(token, 1016, reply=(2,))
        add(token, 2099, write=True, words=(2,))
        add(token, 2099, reply=(2,))
        add(0, 4003, write=True, words=(token, 2), outcome="EXISTING_FIFO_WRITE")
    mode = 2 if scenario == "BARCODE_MISMATCH" else 1
    ack = 4 if scenario == "BARCODE_MISMATCH" else 3
    add(3, 4303, write=True, words=(3, mode))
    add(3, 2099, reply=(1,))
    add(3, 2100, reply=encoded_words("SIM-BAD-END" if mode == 2 else ""))
    add(3, 2099, write=True, words=(ack,))
    add(3, 2099, reply=(ack,))
    add(3, 1016, write=True, words=(2,))
    add(3, 1016, reply=(2,))
    write_wire(root, rows)
    (root / "master.log").write_text(
        "SIM_LEGACY_ABNORMAL_FINS_PASS token=3 scenario=" +
        ("BarcodeMismatch" if mode == 2 else "BarcodeFailure") +
        " steps=D2099,D1016 physical_exit=HOLD\n", encoding="utf8")
    return rows


def write_wire(root, rows):
    (root / "p0-fins-wire.jsonl").write_text(
        "".join(json.dumps(row) + "\n" for row in rows), encoding="utf8")


class EndStationIndependentAuditTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="a5-terminal-wire-fixture-")
        self.root = Path(self.tmp.name)
        self.rows = prepare(self.root)

    def tearDown(self):
        self.tmp.cleanup()

    def outcome(self, scenario="BARCODE_MISMATCH"):
        return verify_same_run(self.root, 3, scenario, 2, 2000)

    def reject(self, code):
        with self.assertRaises(AuditFailure) as err:
            self.outcome()
        self.assertEqual(err.exception.code, code)

    def test_mismatch_raw_fins_two_steps_and_durable_hold_proven(self):
        out = self.outcome()
        self.assertEqual(out["rawFirst"], "D2099=4")
        self.assertEqual(out["rawSecond"], "D1016=2")
        self.assertEqual(out["healthy_neighbor_tokens"], [1, 2])
        self.assertFalse(out["field_verified"])

    def test_scanner_unreadable_must_have_real_empty_d2100_word_reply(self):
        (self.root / "master/sim-full-cycle.db").unlink()
        (self.root / "master/production.db").unlink()
        self.rows = prepare(self.root, "SCAN_FAILED")
        out = self.outcome("SCAN_FAILED")
        self.assertEqual((out["rawFirst"], out["rawSecond"]),
                         ("D2099=3", "D1016=2"))

    def test_raw_sid_tampering_is_detected_in_original_reply_bytes(self):
        row = next(r for r in self.rows if r["Token"] == 3 and r["IsWrite"] and r["Address"] == 2099)
        frame = bytearray.fromhex(row["ResponseHex"])
        frame[9] ^= 0x21
        row["ResponseHex"] = frame.hex()
        write_wire(self.root, self.rows)
        self.reject("FINS_RESPONSE_MISMATCH")

    def test_duplicate_final_udp_write_is_not_hidden_by_good_register_end_values(self):
        row = next(r for r in self.rows if r["Token"] == 3 and r["IsWrite"] and r["Address"] == 1016)
        copied = dict(row)
        sequence = len(self.rows) + 1
        req, rsp = sample_packet(True, 1016, (2,), sequence)
        copied.update(Sequence=sequence, Sid=sequence, RequestHex=req.hex(),
                      ResponseHex=rsp.hex(), MonotonicTimestamp=sequence * 100)
        self.rows.append(copied)
        write_wire(self.root, self.rows)
        self.reject("INCOMPLETE_OR_RETRIED_FINAL")

    def test_missing_first_readback_cannot_be_replaced_by_eventual_good_value(self):
        target = next(r for r in self.rows if r["Token"] == 3 and
                      not r["IsWrite"] and r["Address"] == 2099 and
                      len(r["ResponseHex"]) == 32 and
                      r["Sequence"] > next(w["Sequence"] for w in self.rows
                                           if w["Token"] == 3 and w["IsWrite"] and w["Address"] == 2099))
        request, reply = sample_packet(False, 2099, (), target["Sid"], (1,))
        target.update(RequestHex=request.hex(), ResponseHex=reply.hex())
        write_wire(self.root, self.rows)
        self.reject("FIRST_READBACK_INVALID")

    def test_missing_hold_while_packet_succeeded_is_a_failure(self):
        with closing(sqlite3.connect(self.root / "master/production.db")) as c, c:
            c.execute("DELETE FROM workpiece_holds")
        self.reject("MISSING_OR_DUPLICATE")

    def test_hold_written_after_plc_datagram_fails(self):
        with closing(sqlite3.connect(self.root / "master/production.db")) as c, c:
            c.execute("UPDATE workpiece_holds SET raised_utc='2026-10-17T08:00:01+00:00'")
        self.reject("HOLD_AFTER_PLC_WRITE")

    def test_unverified_output_client_ack_cannot_be_called_success(self):
        (self.root / "master.log").write_text(
            "SIM_LEGACY_ABNORMAL_OUTPUT_UNCERTAIN token=3\n", encoding="utf8")
        self.reject("NOT_VERIFIED")

    def test_any_abnormal_fifo_ejection_is_detected_from_raw_write(self):
        sequence = len(self.rows) + 1
        req, rsp = sample_packet(True, 4003, (3, 2), sequence)
        self.rows.append(dict(Sequence=sequence, ReceivedUtc="2026-10-17T08:00:00+00:00",
                              MonotonicTimestamp=sequence * 100, Sid=sequence,
                              Token=0, IsWrite=True, Address=4003, Values=[3, 2],
                              Outcome="EXISTING_FIFO_WRITE", RequestHex=req.hex(),
                              ResponseHex=rsp.hex()))
        write_wire(self.root, self.rows)
        self.reject("FINS_PHYSICAL_EXIT_COMMAND")


if __name__ == "__main__":
    unittest.main()
