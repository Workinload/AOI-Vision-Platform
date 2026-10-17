#!/usr/bin/env python3
"""Agent05 read-only END-station QA against SAME-RUN five-process raw FINS and SQLite.

No virtual register mirrors count as evidence; no Host or vPLC is created here.
A prepared FINS response is NOT proof that the client received it. For PASS,
require the actual Master post-readback success log as well as the raw server
request/response and durable HOLD/exit ledger evidence. For transport ambiguity
use the separate real-UDP applied-write/lost-ACK NUnit acceptance; do NOT run
this positive-path verifier to "resolve" an uncertain write.
"""
from __future__ import annotations

import argparse
from contextlib import closing
import datetime as dt
import json
from pathlib import Path
import sqlite3
import sys

from p0_fins_timeline_audit import AuditFailure, demand, iso, load_records, parse_trace

ABNORMAL = {
    "SCAN_FAILED": ((2099, 3), (1016, 2), 1, "END_BARCODE_SCAN_FAILED"),
    "BARCODE_MISMATCH": ((2099, 4), (1016, 2), 2, "END_BARCODE_MISMATCH"),
}
NORMAL = {
    "NORMAL_GOOD": ((1016, 1), (2099, 2)),
    "NORMAL_NG": ((1016, 2), (2099, 2)),
}


def sql(path: Path, statement: str, args=()):
    demand(path.is_file(), "NOT_VERIFIED", "Real Master SQLite WAL absent: " + str(path))
    with closing(sqlite3.connect("file:" + path.as_posix() + "?mode=ro", uri=True, timeout=5)) as db:
        return db.execute(statement, args).fetchall()


def one(rows, purpose):
    demand(len(rows) == 1, "MISSING_OR_DUPLICATE",
           f"{purpose}: require ONE actual operation, found {len(rows)}")
    return rows[0]


def decode_scan(words):
    raw = b"".join(w.to_bytes(2, "big") for w in words)
    try:
        return raw.decode("ascii").rstrip("\x00 ")
    except UnicodeDecodeError as exc:
        raise AuditFailure("BAD_PLC_BARCODE_BYTES", str(exc)) from exc


def all_finals(wire, token):
    return [w for w in wire if w.token == token and w.write and
            w.address in (1016, 2099)]


def verify_final(wire, token, expected, offer_scenario, deadline_ms):
    offer = one([w for w in wire if w.token == token and w.address == 4303
                 and w.write and w.ok and w.words == (token, offer_scenario)],
                f"token {token} actual D4303 private external end-scan offer")
    writes = all_finals(wire, token)
    demand(len(writes) == 2 and all(w.ok for w in writes),
           "INCOMPLETE_OR_RETRIED_FINAL",
           f"token {token}: every write needs exactly one successful actual FINS response")
    demand(tuple((w.address, *w.words) for w in writes) == expected,
           "FINAL_FINS_ORDER", f"token {token}: actual={[(w.address,w.words) for w in writes]} expected={expected}")
    demand(writes[0].sid != writes[1].sid,
           "FINS_SID_REUSED", "Two different final writes cannot share a SID in one client session")
    ready = [w for w in wire if w.token == token and w.address == 2099 and
             not w.write and w.ok and w.reply_words == (1,) and
             offer.seq < w.seq < writes[0].seq]
    demand(bool(ready), "SCANNER_REQUEST_NOT_OBSERVED",
           f"token {token}: no actual FINS read D2099=1 before final write")
    for i, w in enumerate(writes):
        finish = writes[i + 1].seq if i == 0 else (1 << 63)
        # FIRST actual readback after write must match. A later correct read
        # cannot conceal an initially stale/wrong ACK/readback.
        reads = [x for x in wire if x.token == token and not x.write and
                 x.address == w.address and w.seq < x.seq < finish]
        demand(bool(reads) and reads[0].ok and reads[0].reply_words == w.words,
               "FIRST_READBACK_INVALID",
               f"token {token}: D{w.address} missing/stale first independent FINS readback")
        demand(reads[0].sid != w.sid,
               "FINS_SID_REUSED", "Readback must be its own FINS request/SID")
    elapsed = (writes[1].utc - ready[0].utc).total_seconds() * 1000
    demand(0 <= elapsed <= deadline_ms, "END_TIMEOUT",
           f"token {token} final FINS pair took {elapsed:.2f}ms, limit={deadline_ms}ms (legacy reference only)")
    demand(writes[0].tick <= writes[1].tick and writes[0].seq < writes[1].seq,
           "FINAL_MONOTONIC_ORDER", "Raw wire local sequence/ticks are contradictory")
    return writes, offer


def verify_same_run(root: Path, token: int, scenario: str, healthy_before: int,
                    deadline_ms: int):
    demand(scenario in ABNORMAL, "INVALID_SCENARIO", scenario)
    ledger = root / "master" / "sim-full-cycle.db"
    master = root / "master" / "production.db"
    wire_path = root / "p0-fins-wire.jsonl"
    log_path = root / "master.log"
    for path in (wire_path, log_path):
        demand(path.is_file(), "NOT_VERIFIED", "Actual five-process source is missing: " + str(path))
    wire = parse_trace(load_records(wire_path))
    contents = log_path.read_text(encoding="utf-8", errors="replace")
    rows = sql(ledger,
               "SELECT token, identity_json, phase FROM sim_full_tokens WHERE token=?", (token,))
    current = one(rows, "fault WorkpieceId/identity in actual FIFO SQLite ledger")
    identity = json.loads(current[1])
    item = identity["workpieceId"]
    demand(bool(item) and identity.get("epoch") is not None and identity.get("releaseId"),
           "MISSING_IDENTITY", "No immutable item identity/Epoch/Release in Master WAL")
    step1, step2, offer_state, hold_code = ABNORMAL[scenario]
    writes, offer = verify_final(wire, token, (step1, step2), offer_state, deadline_ms)
    scan_reads = [w for w in wire if w.token == token and w.address == 2100 and
                  not w.write and w.ok and len(w.reply_words) == 10 and
                  offer.seq < w.seq < writes[0].seq]
    demand(bool(scan_reads), "EXTERNAL_SCANNER_NOT_OBSERVED",
           "Cannot infer scanner outcome solely from Host scenario flag")
    barcode = decode_scan(scan_reads[0].reply_words)
    good_barcode = identity.get("barcode", "")
    demand((not barcode) if scenario == "SCAN_FAILED" else
           bool(barcode) and barcode != good_barcode,
           "SCANNER_STATUS_FALSE", f"PLC D2100 scanned {barcode!r} vs original {good_barcode!r}")
    holds = sql(master,
                "SELECT reason_code, detail, raised_utc FROM workpiece_holds WHERE workpiece_id=?",
                (item,))
    reason, detail, raised = one(holds, "persisted HOLD before abnormal FINS")
    demand(reason == hold_code and "manual reconciliation" in detail,
           "UNSAFE_HOLD_REASON", f"Expected {hold_code}, actually {reason}")
    demand(iso(raised) <= writes[0].utc, "HOLD_AFTER_PLC_WRITE",
           "Master durable HOLD recorded after first FINS datagram; unsafe ordering")
    final = one(sql(master, "SELECT final_json FROM workpieces WHERE workpiece_id=?", (item,)),
                "workpiece final_json")
    demand(final[0] is None, "FALSE_FINAL_DECISION",
           "Faulty barcode workpiece must not become final GOOD/NG")
    demand(current[2] != "exited", "FALSE_PHYSICAL_EXIT",
           "Abnormal end-scan fault illegally exited FIFO")
    demand(not any(w.address == 4003 and w.write and len(w.words) >= 1 and
                   w.words[0] == token for w in wire),
           "FINS_PHYSICAL_EXIT_COMMAND", "Faulty token received actual private D4003 eject WRITE")
    success = f"SIM_LEGACY_ABNORMAL_FINS_PASS token={token} scenario="
    demand(success + ("BarcodeFailure" if scenario == "SCAN_FAILED" else "BarcodeMismatch")
           in contents and
           f"SIM_LEGACY_ABNORMAL_OUTPUT_UNCERTAIN token={token}" not in contents,
           "NOT_VERIFIED",
           "Raw PLC reply is not client confirmation; Master post-readback success log missing/uncertain")
    demand(not any(w.token == token and w.address == 1016 and w.write and w.words == (1,)
                   for w in wire), "FALSE_GOOD_WRITE",
           "Abnormal WorkpieceId has a FINS D1016=1 GOOD packet")
    proven_healthy = []
    for before in range(1, healthy_before + 1):
        healthy = one(sql(ledger, "SELECT token,identity_json,phase FROM sim_full_tokens WHERE token=?",
                          (before,)), f"healthy token {before}")
        demand(before < token and healthy[2] == "exited",
               "HEALTHY_NEIGHBOR_NOT_EXITED", f"token {before} did not remain safely independent")
        normal, _ = verify_final(wire, before, NORMAL["NORMAL_NG"], 0, deadline_ms)
        demand(any(w.address == 4003 and w.write and
                   w.words == (before, 2) for w in wire),
               "HEALTHY_NEIGHBOR_NOT_EJECTED",
               f"token {before} did not receive real D4003 FIFO ACK")
        proven_healthy.append(before)
    # Do not allow later blocked pieces to receive final FINS commands while
    # an earlier held item still occupies the FIFO front.
    demand(not any(w.token > token and w.address in (1016, 2099) and w.write
                   for w in wire), "LATER_WORKPIECE_OVERTAKE",
           "A blocked front item was overtaken by a different workpiece")
    return {
        "status": "PASS_SAME_RUN_ABNORMAL_RAW_FINS_AND_WAL",
        "token": token, "workpieceId": item, "scenario": scenario,
        "rawFirst": f"D{writes[0].address}={writes[0].words[0]}",
        "rawSecond": f"D{writes[1].address}={writes[1].words[0]}",
        "sids": [writes[0].sid, writes[1].sid],
        "readbacks": 2, "held_before_first_udp": True,
        "wrong_code_good_writes": 0, "false_fifo_ejections": 0,
        "healthy_neighbor_tokens": proven_healthy,
        "field_verified": False, "localhost_sim_only": True,
    }


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--evidence-root", required=True, type=Path)
    p.add_argument("--token", type=int, default=3)
    p.add_argument("--scenario", choices=tuple(ABNORMAL), required=True)
    p.add_argument("--healthy-before", type=int, default=2)
    p.add_argument("--end-deadline-ms", type=int, default=2000,
                   help="Historical test profile only; not measured OEM PLC timing")
    p.add_argument("--report", type=Path)
    args = p.parse_args(argv)
    try:
        demand(args.token > 0 and 0 <= args.healthy_before < args.token and
               1 <= args.end_deadline_ms <= 300000,
               "INVALID_INPUT", "Invalid item, healthy neighbor count or test deadline")
        outcome = verify_same_run(args.evidence_root.resolve(), args.token, args.scenario,
                                  args.healthy_before, args.end_deadline_ms)
        code = 0
        print("A5_TERMINAL_RAW_FINS_PASS " + json.dumps(outcome), flush=True)
    except AuditFailure as exc:
        code = 3 if exc.code == "NOT_VERIFIED" else 1
        outcome = {"status": "NOT_VERIFIED" if code == 3 else "FAIL",
                   "reason": exc.code, "detail": str(exc)}
        print("A5_TERMINAL_RAW_FINS_" + outcome["status"] + " " +
              json.dumps(outcome, ensure_ascii=False), file=sys.stderr)
    except (OSError, ValueError, KeyError, sqlite3.Error, TypeError) as exc:
        code = 1
        outcome = {"status": "FAIL", "reason": "EVIDENCE_PARSE", "detail": str(exc)}
        print("A5_TERMINAL_RAW_FINS_FAIL " + json.dumps(outcome), file=sys.stderr)
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(outcome, ensure_ascii=False, indent=2) + "\n",
                               encoding="utf-8")
    return code


if __name__ == "__main__":
    raise SystemExit(main())
