#!/usr/bin/env python3
"""Agent05 READ-ONLY terminal FINS after-apply fault + restart evidence audit.

Run against evidence from the genuine SIM_ONLY five-OS-process AOI Host; this
program NEVER sends FINS packets, restarts a Core, or fabricates PLC responses.

The vPLC-generated response is NOT assumed to reach the client. An applied
write with absent response remains UNKNOWN to the Host. A fresh independent
postfault UDP READ must be in the same server's raw wire log; only then can the
auditor report the PLC's observed register values (not a guess from the mirror).

Exit 0: all required evidence observed and internally consistent.
Exit 1: contradictory / unsafe evidence.
Exit 3: missing actual Host, readback or restart evidence (NOT_VERIFIED).

This is a standalone QA increment; A0 owns Host, A1 the effect journal, A2 the
localhost-only shadow, A7 the persistent event recorder. No field authorization.
"""
from __future__ import annotations

import argparse
from contextlib import closing
from dataclasses import dataclass
from datetime import datetime, timezone
import json
from pathlib import Path
import re
import sqlite3
import struct
import sys
from typing import Any


class AuditFailure(Exception):
    def __init__(self, code: str, detail: str):
        self.code = code
        super().__init__(detail)


def require(test: bool, code: str, detail: str) -> None:
    if not test:
        raise AuditFailure(code, detail)


def get(row: dict, key: str, default: Any = None) -> Any:
    return row.get(key, row.get(key[0].upper() + key[1:], default))


def parse_time(value: Any) -> datetime:
    require(isinstance(value, str), "NOT_VERIFIED", "Missing recorded UTC")
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as exc:
        raise AuditFailure("BAD_TIMESTAMP", str(exc)) from exc
    require(parsed.tzinfo is not None, "BAD_TIMESTAMP", "UTC offset required")
    return parsed.astimezone(timezone.utc)


def bytes_from_hex(value: Any, purpose: str) -> bytes:
    require(isinstance(value, str), "NOT_VERIFIED", "Missing actual raw " + purpose)
    try:
        return bytes.fromhex(value)
    except ValueError as exc:
        raise AuditFailure("BAD_FINS_HEX", purpose) from exc


@dataclass(frozen=True)
class Fins:
    sequence: int
    token: int
    timestamp: datetime
    tick: int
    sid: int
    address: int
    write: bool
    words: tuple[int, ...]
    readback: tuple[int, ...]
    outcome: str
    endcode: int | None
    response_present: bool

    @property
    def acked(self) -> bool:
        return self.endcode == 0 and self.response_present


def read_fins(row: dict) -> Fins:
    req = bytes_from_hex(get(row, "requestHex"), "FINS request")
    resp = bytes_from_hex(get(row, "responseHex"), "FINS response")
    require(len(req) >= 18 and req[:3] == bytes((0x80, 0, 2)) and
            req[10] == 1 and req[11] in (1, 2) and req[12] == 0x82 and
            req[15] == 0, "INVALID_FINS_REQUEST", "Expected FINS DM 0101/0102")
    sid = req[9]
    address, count = struct.unpack_from(">H", req, 13)[0], struct.unpack_from(">H", req, 16)[0]
    write = req[11] == 2
    require(0 < count <= 64 and address + count <= 5000 and
            len(req) == 18 + (2 * count if write else 0),
            "INVALID_FINS_LENGTH", f"D{address} count {count}")
    words = struct.unpack_from(">" + str(count) + "H", req, 18) if write else ()
    require(req[4] > 0 and req[7] > 0 and req[4] != req[7],
            "INVALID_FINS_ROUTE", "Source/destination FINS nodes invalid")
    endcode = None
    readback: tuple[int, ...] = ()
    if resp:
        require(len(resp) >= 14 and resp[:3] == bytes((0xC0, 0, 2)) and
                resp[3:6] == req[6:9] and resp[6:9] == req[3:6] and
                resp[9:12] == req[9:12], "INVALID_FINS_SID_RESPONSE", f"D{address} SID={sid}")
        endcode = struct.unpack_from(">H", resp, 12)[0]
        require(len(resp) == (14 if write or endcode else 14 + 2 * count),
                "INVALID_FINS_RESPONSE_LENGTH", f"D{address}")
        if not write and endcode == 0:
            readback = struct.unpack_from(">" + str(count) + "H", resp, 14)
    require(get(row, "sid") == sid and get(row, "address") == address and
            get(row, "isWrite") is write and tuple(get(row, "values", [])) == tuple(words),
            "FINS_METADATA_MISMATCH", f"Metadata does not match raw datagram D{address}")
    seq, token, tick = get(row, "sequence"), get(row, "token"), get(row, "monotonicTimestamp")
    require(type(seq) is int and seq > 0 and type(tick) is int and tick > 0 and
            type(token) is int and 0 <= token <= 65535,
            "NOT_VERIFIED", "Missing vPLC transaction sequence/token/monotonic tick")
    outcome = get(row, "outcome")
    require(isinstance(outcome, str) and bool(outcome), "NOT_VERIFIED", "Missing vPLC outcome")
    require(outcome != "OK" or (resp and endcode == 0),
            "FALSE_FINS_SUCCESS", "A2 claimed success with missing/nonzero response")
    require(endcode in (None, 0) or outcome != "OK",
            "FALSE_FINS_SUCCESS", "A2 ignored a nonzero FINS end code")
    return Fins(seq, token, parse_time(get(row, "receivedUtc")), tick, sid, address,
                write, tuple(words), tuple(readback), outcome, endcode, bool(resp))


def read_wire(path: Path) -> list[Fins]:
    require(path.is_file(), "NOT_VERIFIED", "Missing real vPLC raw FINS transcript: " + str(path))
    try:
        lines = [json.loads(line) for line in path.read_text(encoding="utf-8-sig").splitlines()
                 if line.strip()]
    except (ValueError, OSError) as exc:
        raise AuditFailure("BAD_WIRE_EVIDENCE", str(exc)) from exc
    require(bool(lines), "NOT_VERIFIED", "No actual FINS transactions")
    frames = [read_fins(row) for row in lines]
    for previous, current in zip(frames, frames[1:]):
        require(current.sequence == previous.sequence + 1 and
                current.tick >= previous.tick and current.timestamp >= previous.timestamp,
                "WIRE_REORDERED", "vPLC sequence/monotonic/UTC moved backward")
    return frames


def sql(path: Path, statement: str, args: tuple = ()) -> list[tuple]:
    require(path.is_file(), "NOT_VERIFIED", "Missing Master/Slave production WAL: " + str(path))
    # contextlib.closing is intentional: sqlite3's context manager commits a
    # transaction but DOES NOT close file handles on Windows.
    with closing(sqlite3.connect(path.as_uri() + "?mode=ro", uri=True, timeout=5)) as con:
        return con.execute(statement, args).fetchall()


def exactly_one(values: list, label: str):
    require(len(values) == 1, "MISSING_OR_DUPLICATE", f"{label}: expected 1, found {len(values)}")
    return values[0]


def barcode_from_words(words: tuple[int, ...]) -> str:
    require(len(words) == 10, "BAD_SCANNER_WORDS", "Need actual 10 DM words")
    try:
        return b"".join(word.to_bytes(2, "big") for word in words).decode("ascii").rstrip("\0 ")
    except (UnicodeDecodeError, OverflowError) as exc:
        raise AuditFailure("BAD_SCANNER_WORDS", str(exc)) from exc


FAULTS = ("ack-first", "ack-second", "reject-first")
SCENARIOS = ("mismatch", "unreadable")


def check(root: Path, scenario: str, fault: str, require_restart: bool = True) -> dict:
    require(scenario in SCENARIOS and fault in FAULTS, "INVALID_INPUT", "Unknown scenario/fault")
    ledger = root / "master" / "sim-full-cycle.db"
    master = root / "master" / "production.db"
    slave = root / "slave" / "production.db"
    item_row = exactly_one(sql(ledger, "SELECT identity_json,phase FROM sim_full_tokens WHERE token=3"),
                           "Faulty token3")
    item = json.loads(item_row[0]); item_id = item.get("workpieceId")
    require(bool(item_id) and item.get("epoch") is not None and bool(item.get("releaseId")),
            "INVALID_ITEM_IDENTITY", "Workpiece identity, Epoch, Release missing")
    require(item_row[1] == "registered", "UNSAFE_FAULT_EJECTION", "Held token3 was removed")
    code = "END_BARCODE_MISMATCH" if scenario == "mismatch" else "END_BARCODE_SCAN_FAILED"
    hold = exactly_one(sql(master, "SELECT reason_code, detail, raised_utc FROM workpiece_holds WHERE workpiece_id=?", (item_id,)),
                       "Persisted fault HOLD")
    require(hold[0] == code and "manual reconciliation" in hold[1],
            "WRONG_HOLD_REASON", str(hold))
    for path, expected in ((master, 33), (slave, 18)):
        count = exactly_one(sql(path, "SELECT COUNT(*) FROM channel_results"), "channel count")[0]
        require(count == expected, "NOT_VERIFIED", f"Actual PNG channel results {count} != {expected}")
    require(exactly_one(sql(master, "SELECT final_json FROM workpieces WHERE workpiece_id=?", (item_id,)),
                        "Faulty item's persistent Final")[0] is None,
            "FALSE_QUALITY_FINAL", "Abnormal item was assigned Good/Reject final")
    for healthy in (1, 2):
        row = exactly_one(sql(ledger, "SELECT phase FROM sim_full_tokens WHERE token=?", (healthy,)),
                          "Healthy predecessor token")
        require(row[0] == "exited", "NOT_VERIFIED", f"Healthy predecessor {healthy} not finished")

    wires = read_wire(root / "p0-fins-wire.jsonl")
    # The unaffected predecessors must have their own real FINS terminal
    # exchanges; two "exited" SQLite labels are insufficient evidence.
    for healthy in (1, 2):
        verified = [w for w in wires if w.token == healthy and w.write and
                    w.address in (1016, 2099)]
        require(len(verified) == 2 and
                [(w.address, w.words) for w in verified] ==
                [(1016, (2,)), (2099, (2,))] and
                all(w.acked for w in verified) and verified[0].sid != verified[1].sid,
                "HEALTHY_PREDECESSOR_FINS_MISSING",
                f"token {healthy} lacks actual quality-first D1016=2 -> D2099=2")
        for i, written in enumerate(verified):
            upper = verified[i + 1].sequence if i == 0 else (1 << 62)
            reads = [w for w in wires if w.token == healthy and not w.write and
                     w.address == written.address and
                     written.sequence < w.sequence < upper]
            require(bool(reads) and reads[0].acked and reads[0].readback == written.words,
                    "HEALTHY_PREDECESSOR_READBACK_MISSING",
                    f"token {healthy} D{written.address} first readback absent/wrong")
        require(any(w.address == 4003 and w.write and w.acked and
                    w.words == (healthy, 2) for w in wires),
                "HEALTHY_PREDECESSOR_EXIT_MISSING",
                f"token {healthy} has no actual FINS D4003 physical exit")
    first_value = 4 if scenario == "mismatch" else 3
    fault_writes = [w for w in wires if w.token == 3 and w.write and w.address in (2099, 1016)]
    expected = [(2099, first_value), (1016, 2)] if fault == "ack-second" else [(2099, first_value)]
    require([(w.address, *w.words) for w in fault_writes] == expected,
            "UNSAFE_FINS_REWRITE", f"Actual terminal writes {[(w.address, w.words) for w in fault_writes]} expected {expected}")
    first = fault_writes[0]
    require(parse_time(hold[2]) <= first.timestamp, "HOLD_AFTER_PLC_WRITE", "HOLD must precede first physical write")
    injected = fault_writes[-1]
    if fault in ("ack-first", "ack-second"):
        require(injected.outcome == "INJECTED_TERMINAL_ACK_LOST_AFTER_APPLY" and
                not injected.response_present and injected.endcode is None,
                "ACK_LOSS_NOT_PROVEN", "Must apply PLC side effect, then drop raw UDP reply")
    else:
        require(injected.outcome == "INJECTED_TERMINAL_WRITE_REJECTED" and
                injected.response_present and injected.endcode == 5,
                "WRITE_REFUSAL_NOT_PROVEN", "Require real negative FINS end code 0x0005")
    if fault == "ack-second":
        require(first.acked and first.sid != injected.sid,
                "FIRST_STEP_NOT_CONFIRMED", "First D2099 write must be acknowledged with independent SID")
        before_second = [x for x in wires if x.token == 3 and not x.write and x.address == 2099 and
                         first.sequence < x.sequence < injected.sequence]
        require(bool(before_second) and before_second[0].acked and
                before_second[0].readback == (first_value,),
                "FIRST_READBACK_INVALID", "The FIRST readback must equal the first write")
    offer = exactly_one([w for w in wires if w.token == 3 and w.write and
                         w.address == 4303 and
                         w.words == (3, 2 if scenario == "mismatch" else 1) and
                         w.acked and w.sequence < first.sequence],
                        "External vPLC D4303 end-scan offer")
    ready = [w for w in wires if w.token == 3 and not w.write and w.address == 2099 and
             w.acked and w.readback == (1,) and
             offer.sequence < w.sequence < first.sequence]
    require(bool(ready), "NO_REAL_END_SCAN_OFFER", "No actual D2099=1 from PLC")
    scanner = [w for w in wires if w.token == 3 and not w.write and w.address == 2100 and
               w.acked and len(w.readback) == 10 and
               offer.sequence < w.sequence < first.sequence]
    require(bool(scanner), "NO_REAL_SCANNER_INPUT", "D2100 barcode must be read from PLC")
    scanned = barcode_from_words(scanner[-1].readback)
    require((not scanned) if scenario == "unreadable" else (bool(scanned) and scanned != item.get("barcode")),
            "FALSE_BARCODE_FAULT", "Actual D2100 did not prove the declared scanner fault")
    if scenario == "unreadable":
        reads = [w for w in wires if w.token == 3 and w.address == 2020 and not w.write and
                 w.response_present and w.endcode == 0 and len(w.readback) == 10 and
                 w.outcome == "INJECTED_END_SCAN_UNREADABLE"]
        require(bool(reads) and barcode_from_words(reads[-1].readback) == "",
                "FALSE_UNREADABLE_SCANNER", "No real external D2020 unreadable result")

    logpath = root / "master.log"
    require(logpath.is_file(), "NOT_VERIFIED", "Missing actual Host Master process log")
    log = logpath.read_text(encoding="utf-8", errors="replace")
    expected_count = 1 if fault == "ack-second" else 0
    require(f"SIM_LEGACY_ABNORMAL_OUTPUT_UNCERTAIN token=3" in log and
            f"confirmed_steps={expected_count}" in log and "no_automatic_retry=1" in log,
            "NOT_VERIFIED", "Master did not disclose uncertain terminal effect and step count")

    # All following diagnostics must be ACTUAL FINS READ requests, never a
    # final PLC memory dictionary or a manufactured third write.
    last = fault_writes[-1].sequence
    want = {2099: 1 if fault == "reject-first" else first_value,
            1016: 2 if fault == "ack-second" else 0}
    read_ids = []
    for address, expected_word in want.items():
        reads = [w for w in wires if w.token == 3 and not w.write and
                 w.address == address and w.sequence > last and w.acked]
        require(bool(reads), "NOT_VERIFIED",
                f"No actual post-fault FINS/UDP readback D{address}")
        require(reads[-1].readback == (expected_word,),
                "POSTFAULT_READBACK_MISMATCH",
                f"Observed raw FINS D{address}={reads[-1].readback}, expected {expected_word}")
        read_ids.append(reads[-1].sid)
    # A fresh connection may restart its SID counter, so ONLY demand a new
    # datagram with larger server sequence; never infer identity from SID alone.
    require(len([w for w in wires if w.token == 3 and w.write and w.address in (2099,1016)]) == len(expected),
            "UNSAFE_FINS_REWRITE", "Extra FINS write detected after read-only recovery")
    require(not any(w.write and w.address == 4003 and w.words and w.words[0] == 3 for w in wires),
            "FALSE_PHYSICAL_EJECTION", "Faulty token received D4003 actual ejection WRITE")
    require(not any(w.token == 3 and w.write and w.address == 1016 and w.words == (1,) for w in wires),
            "FALSE_GOOD", "Abnormal item received D1016=1")
    if require_restart:
        restart = root / "master-restart-probe.log"
        require(restart.is_file(), "NOT_VERIFIED", "No actual OS Master restart probe transcript")
        text = restart.read_text(encoding="utf-8", errors="replace")
        require("SIM_FULL_RESTART_HOLD" in text and
                "SIM_FULL_EXITED token=3" not in text and
                "SIM_LEGACY_ABNORMAL_FINS_PASS token=3" not in text,
                "RESTART_REPLAY_UNCERTAIN", "Restart guard unproven/unsafe")
        # The caller's driver is responsible for recording nonzero exit code
        # and checking no new write packets after giving exporter time to flush.
        manifest = root / "a5-terminal-fault-evidence.json"
        require(manifest.is_file(), "NOT_VERIFIED", "No five-process test completion manifest")
        result = json.loads(manifest.read_text(encoding="utf-8"))
        require(result.get("masterRestartBlocked") is True and
                result.get("noUnsafeFinsReplay") is True and
                result.get("workpieceId") == item_id,
                "NOT_VERIFIED", "Actual driver did not attest a completed restart probe")
    return {"status": "PASS_A5_AFTER_APPLY_RAW_FINS_WAL", "workpieceId": item_id,
            "scenario": scenario, "fault": fault, "terminalWrites": len(fault_writes),
            "faultWriteSid": injected.sid, "faultWriteOutcome": injected.outcome,
            "confirmedStepsFromMaster": expected_count, "postFaultReadSids": read_ids,
            "readbackD2099": want[2099], "readbackD1016": want[1016],
            "holdBeforeWrite": True, "faultyItemEjected": False,
            "restartEvidence": require_restart, "fieldVerified": False, "simOnly": True}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evidence-root", required=True, type=Path)
    parser.add_argument("--scenario", required=True, choices=SCENARIOS)
    parser.add_argument("--fault", required=True, choices=FAULTS)
    parser.add_argument("--wire-only", action="store_true", help="Skip unavailable OS restart proof; success is limited to wire/WAL")
    parser.add_argument("--report", type=Path)
    args = parser.parse_args(argv)
    try:
        result = check(args.evidence_root.resolve(), args.scenario, args.fault,
                       require_restart=not args.wire_only)
        code = 0
        print("A5_P0_AFTER_APPLY_WIRE_PASS " + json.dumps(result, separators=(",", ":")))
    except AuditFailure as exc:
        code = 3 if exc.code == "NOT_VERIFIED" else 1
        result = {"status": "NOT_VERIFIED" if code == 3 else "FAIL",
                  "reason": exc.code, "detail": str(exc), "fieldVerified": False}
        print("A5_P0_AFTER_APPLY_WIRE_" + result["status"] + " " +
              json.dumps(result, ensure_ascii=False), file=sys.stderr)
    except (sqlite3.Error, ValueError, TypeError, OSError, KeyError, IndexError) as exc:
        code = 1
        result = {"status": "FAIL", "reason": "EVIDENCE_PARSE", "detail": str(exc)}
        print("A5_P0_AFTER_APPLY_WIRE_FAIL " + json.dumps(result), file=sys.stderr)
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return code


if __name__ == "__main__":
    raise SystemExit(main())
