"""Agent05 negative protocol/WAL fixture tests (NOT five-OS-process Host evidence).

The CLI's PASS is only valid when fed SAME-RUN real Host/vPLC artifacts.
These synthetic bytes deliberately exercise the fail-closed independent parser.
"""
from __future__ import annotations

from contextlib import closing
import json
from pathlib import Path
import sqlite3
import struct
import tempfile
import unittest
import uuid

from p0_after_apply_wire_reconcile import AuditFailure, check


def make_packet(write: bool, address: int, sid: int, *, values=(), readback=(), endcode=0,
                drop=False):
    count = len(values) if write else len(readback)
    req = bytearray(18 + (2 * count if write else 0))
    req[:10] = bytes([0x80, 0, 2, 0, 50, 0, 0, 130, 0, sid])
    req[10:13] = bytes([1, 2 if write else 1, 0x82])
    struct.pack_into(">H", req, 13, address)
    struct.pack_into(">H", req, 16, count)
    if write:
        struct.pack_into(">" + "H" * count, req, 18, *values)
    if drop:
        return req.hex(), ""
    rep = bytearray(14 + (0 if write or endcode else count * 2))
    rep[:12] = req[:12]
    rep[0] = 0xC0
    rep[3:9] = req[6:9] + req[3:6]
    struct.pack_into(">H", rep, 12, endcode)
    if not write and endcode == 0:
        struct.pack_into(">" + "H" * count, rep, 14, *readback)
    return req.hex(), rep.hex()


def pack_ascii(text: str):
    b = text.encode("ascii").ljust(20, b"\0")
    return tuple(int.from_bytes(b[i:i+2], "big") for i in range(0, 20, 2))


def sqlite_create(path: Path, tables: dict[str, tuple[str, list[tuple[str, tuple]]]]):
    path.parent.mkdir(exist_ok=True, parents=True)
    with closing(sqlite3.connect(path)) as db, db:
        for schema, entries in tables.values():
            db.execute(schema)
            for command, args in entries:
                db.execute(command, args)


class AfterAppliedWireFixture(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="a5-after-apply-")
        self.root = Path(self.tmp.name)
        self.scenario = "mismatch"
        self.fault = "ack-first"
        self.item = str(uuid.uuid4())
        self.frames = []
        self.make_fixture(self.scenario, self.fault)

    def tearDown(self):
        self.tmp.cleanup()

    def append(self, address, write, values=(), readback=(), outcome="OK", *,
               drop=False, endcode=0, token=3):
        sid = len(self.frames) + 1
        request, reply = make_packet(write, address, sid, values=values,
                                     readback=readback, drop=drop, endcode=endcode)
        self.frames.append(dict(Sequence=sid, Token=token,
                                ReceivedUtc="2026-10-17T08:00:00+00:00",
                                MonotonicTimestamp=sid * 100, Sid=sid,
                                Address=address, IsWrite=write, Values=list(values),
                                Outcome=outcome, RequestHex=request, ResponseHex=reply))
        return self.frames[-1]

    def store_wire(self):
        (self.root / "p0-fins-wire.jsonl").write_text(
            "".join(json.dumps(v) + "\n" for v in self.frames), encoding="utf8")

    def make_fixture(self, scenario, fault):
        self.scenario, self.fault = scenario, fault
        identity = dict(workpieceId=self.item, barcode="SIM-0003", epoch=1,
                        releaseId="SIM_TEST", traceId=str(uuid.uuid4()))
        sqlite_create(self.root / "master/sim-full-cycle.db", {
            "items": ("CREATE TABLE sim_full_tokens(token INT, identity_json TEXT, phase TEXT)",
                      [("INSERT INTO sim_full_tokens VALUES(?,?,?)",
                        (token, json.dumps(identity if token == 3 else
                                           dict(identity, workpieceId=str(uuid.uuid4()))),
                         "registered" if token == 3 else "exited")) for token in range(1, 4)])
        })
        sqlite_create(self.root / "master/production.db", {
            "holds": ("CREATE TABLE workpiece_holds(workpiece_id TEXT, reason_code TEXT, detail TEXT,raised_utc TEXT)",
                      [("INSERT INTO workpiece_holds VALUES(?,?,?,?)",
                        (self.item, "END_BARCODE_MISMATCH" if scenario == "mismatch" else
                         "END_BARCODE_SCAN_FAILED", "manual reconciliation required",
                         "2026-10-17T08:00:00+00:00"))]),
            "wp": ("CREATE TABLE workpieces(workpiece_id TEXT,final_json TEXT)",
                   [("INSERT INTO workpieces VALUES(?,NULL)", (self.item,))]),
            "channels": ("CREATE TABLE channel_results (id INT)",
                         [("INSERT INTO channel_results VALUES(?)", (i,)) for i in range(33)])
        })
        sqlite_create(self.root / "slave/production.db", {
            "channels": ("CREATE TABLE channel_results (id INT)",
                         [("INSERT INTO channel_results VALUES(?)", (i,)) for i in range(18)])
        })
        status = 4 if scenario == "mismatch" else 3
        self.frames = []
        # Separate raw FINS success/readbacks and ejection for healthy tokens,
        # never substitute SQLite phase for actual UDP protocol proof.
        for predecessor in (1, 2):
            self.append(4303, True, values=(predecessor, 0), token=predecessor)
            self.append(2099, False, readback=(1,), token=predecessor)
            self.append(1016, True, values=(2,), token=predecessor)
            self.append(1016, False, readback=(2,), token=predecessor)
            self.append(2099, True, values=(2,), token=predecessor)
            self.append(2099, False, readback=(2,), token=predecessor)
            self.append(4003, True, values=(predecessor, 2), token=0,
                        outcome="EXISTING_FIFO_WRITE")
        if scenario == "unreadable":
            self.append(2020, False, readback=pack_ascii(""),
                        outcome="INJECTED_END_SCAN_UNREADABLE")
        self.append(4303, True, values=(3, 2 if scenario == "mismatch" else 1))
        self.append(2099, False, readback=(1,))
        self.append(2100, False, readback=pack_ascii("BAD-SCAN" if scenario == "mismatch" else ""))
        if fault == "reject-first":
            self.append(2099, True, values=(status,), endcode=5,
                        outcome="INJECTED_TERMINAL_WRITE_REJECTED")
        elif fault == "ack-first":
            self.append(2099, True, values=(status,), drop=True,
                        outcome="INJECTED_TERMINAL_ACK_LOST_AFTER_APPLY")
        else:
            self.append(2099, True, values=(status,))
            self.append(2099, False, readback=(status,))
            self.append(1016, True, values=(2,), drop=True,
                        outcome="INJECTED_TERMINAL_ACK_LOST_AFTER_APPLY")
        self.append(2099, False, readback=(1 if fault == "reject-first" else status,))
        self.append(1016, False, readback=(2 if fault == "ack-second" else 0,))
        self.store_wire()
        confirmed = 1 if fault == "ack-second" else 0
        (self.root / "master.log").write_text(
            f"SIM_LEGACY_ABNORMAL_OUTPUT_UNCERTAIN token=3 workpiece={self.item} "
            f"confirmed_steps={confirmed} no_automatic_retry=1\n", encoding="utf8")
        (self.root / "master-restart-probe.log").write_text(
            "SIM_FULL_RESTART_HOLD original_master=terminated\n", encoding="utf8")
        (self.root / "a5-terminal-fault-evidence.json").write_text(
            json.dumps(dict(masterRestartBlocked=True, noUnsafeFinsReplay=True,
                            workpieceId=self.item)), encoding="utf8")

    def verify(self, require_restart=True):
        return check(self.root, self.scenario, self.fault, require_restart=require_restart)

    def expect_failure(self, code, require_restart=True):
        with self.assertRaises(AuditFailure) as thrown:
            self.verify(require_restart)
        self.assertEqual(thrown.exception.code, code)

    def test_lost_ack_first_mismatch_with_independent_real_bytes_fixture(self):
        result = self.verify()
        self.assertEqual(result["readbackD2099"], 4)
        self.assertEqual(result["readbackD1016"], 0)
        self.assertEqual(result["terminalWrites"], 1)

    def test_lost_ack_first_unreadable_external_scan(self):
        self.tmp.cleanup()
        self.tmp = tempfile.TemporaryDirectory(prefix="a5-after-apply-")
        self.root = Path(self.tmp.name)
        self.make_fixture("unreadable", "ack-first")
        self.assertEqual(self.verify()["readbackD2099"], 3)

    def test_lost_ack_second_keeps_only_first_step_confirmed(self):
        self.tmp.cleanup()
        self.tmp = tempfile.TemporaryDirectory(prefix="a5-after-apply-")
        self.root = Path(self.tmp.name)
        self.make_fixture("mismatch", "ack-second")
        self.assertEqual(self.verify()["terminalWrites"], 2)
        self.assertEqual(self.verify()["confirmedStepsFromMaster"], 1)

    def test_explicit_rejection_has_real_negative_endcode(self):
        self.tmp.cleanup()
        self.tmp = tempfile.TemporaryDirectory(prefix="a5-after-apply-")
        self.root = Path(self.tmp.name)
        self.make_fixture("unreadable", "reject-first")
        self.assertEqual(self.verify()["readbackD2099"], 1)

    def test_stale_metadata_cannot_hide_duplicate_real_write(self):
        self.append(2099, True, values=(4,), token=3)
        self.store_wire()
        self.expect_failure("UNSAFE_FINS_REWRITE")

    def test_healthy_sql_exit_without_real_fins_wire_is_rejected(self):
        self.frames = [w for w in self.frames if w["Token"] not in (1, 2)
                       and not (w["Address"] == 4003 and w["Token"] == 0)]
        for i, row in enumerate(self.frames, 1):
            req, rep = make_packet(row["IsWrite"], row["Address"], i,
                                   values=row["Values"],
                                   readback=tuple(int.from_bytes(bytes.fromhex(row["ResponseHex"])[j:j+2], "big")
                                                  for j in range(14, len(bytes.fromhex(row["ResponseHex"])), 2))
                                   if not row["IsWrite"] else (),
                                   drop=(row["ResponseHex"] == ""),
                                   endcode=0)
            row.update(Sequence=i, Sid=i, MonotonicTimestamp=i*100,
                       RequestHex=req, ResponseHex=rep)
        self.store_wire()
        self.expect_failure("HEALTHY_PREDECESSOR_FINS_MISSING")

    def test_sid_tamper_is_a_hard_failure(self):
        frame = bytearray.fromhex(self.frames[-2]["ResponseHex"])
        frame[9] ^= 0x11
        self.frames[-2]["ResponseHex"] = frame.hex()
        self.store_wire()
        self.expect_failure("INVALID_FINS_SID_RESPONSE")

    def test_postfault_physical_ejection_cannot_be_ignored(self):
        self.append(4003, True, values=(3, 2), token=0)
        self.store_wire()
        self.expect_failure("FALSE_PHYSICAL_EJECTION")

    def test_missing_restart_proof_not_automatically_passed(self):
        (self.root / "master-restart-probe.log").unlink()
        self.expect_failure("NOT_VERIFIED")
        self.assertEqual(self.verify(require_restart=False)["terminalWrites"], 1)

    def test_wrong_expected_plc_status_does_not_count_as_verified(self):
        last_read = next(w for w in reversed(self.frames) if w["Token"] == 3 and
                         w["Address"] == 2099 and not w["IsWrite"])
        req, rep = make_packet(False, 2099, last_read["Sid"], readback=(1,))
        last_read.update(RequestHex=req, ResponseHex=rep)
        self.store_wire()
        self.expect_failure("POSTFAULT_READBACK_MISMATCH")

    def test_missing_both_postfault_reads_is_not_verified(self):
        self.frames = [w for w in self.frames if not (
            w["Token"] == 3 and not w["IsWrite"] and
            w["Address"] in (1016, 2099) and w["Sequence"] >
            next(x["Sequence"] for x in self.frames if x["Token"] == 3 and
                 x["IsWrite"] and x["Address"] == 2099))]
        self.store_wire()
        # Truncated trailing reads cannot prove the PLC state; this is an
        # evidence gap, not grounds for an invented successful readback.
        self.expect_failure("NOT_VERIFIED")

    def test_wrong_source_fins_header_metadata_denied(self):
        self.frames[-1]["Address"] = 2099
        self.store_wire()
        self.expect_failure("FINS_METADATA_MISMATCH")

    def test_hold_postdates_physical_side_effect_is_unsafe(self):
        with closing(sqlite3.connect(self.root / "master/production.db")) as db, db:
            db.execute("UPDATE workpiece_holds SET raised_utc=?",
                       ("2026-10-17T08:00:01+00:00",))
        self.expect_failure("HOLD_AFTER_PLC_WRITE")

    def test_output_clash_not_badged_as_field_verified(self):
        result = self.verify()
        self.assertTrue(result["simOnly"])
        self.assertFalse(result["fieldVerified"])


if __name__ == "__main__":
    unittest.main()
