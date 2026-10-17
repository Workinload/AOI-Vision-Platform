#!/usr/bin/env python3
"""Agent05 independent READ-ONLY peer-break/restart evidence verifier.

Consumes data from A0's ACTUAL five-OS-process e2e_peer_loss_interlock.py
runner. Parse raw FINS request/response bytes independently: a forged
Address/IsWrite JSON field cannot disguise a post-reconnect D1016/D2099
write or private D4003 ejection. Does not launch mock peers, reset latches,
perform PLC IO, or invent a successful process restart test.
"""
from __future__ import annotations

import argparse
from contextlib import closing
import json
from pathlib import Path
import re
import sqlite3
import sys

from p0_fins_timeline_audit import AuditFailure, demand, iso, load_records, parse_trace


def sql(path: Path, command: str, args=()):
    demand(path.is_file(), "NOT_VERIFIED", "Missing actual Core WAL " + str(path))
    with closing(sqlite3.connect("file:" + path.as_posix() + "?mode=ro",
                                 uri=True, timeout=5)) as c:
        return c.execute(command, args).fetchall()


def single(rows, label: str):
    demand(len(rows) == 1, "WAL_STATE_INCONSISTENT",
           f"{label}: one durable row required, got {len(rows)}")
    return rows[0]


def check_evidence(root: Path, driver_log: Path):
    demand(driver_log.is_file(), "NOT_VERIFIED",
           "A0 actual five-process starter/restart probe log is required")
    driver = driver_log.read_text(encoding="utf8", errors="replace")
    started = re.findall(r"A0_PEER_LOSS_STARTED name=([\w-]+) pid=(\d+)", driver)
    names = {name for name, pid in started}
    demand(names == {"vplc", "master", "slave", "master-ui", "slave-ui"} and
           len(started) == 5 and len({pid for _, pid in started}) == 5,
           "NOT_VERIFIED", "Five DISTINCT actual OS process launches not observed")
    demand("A0_PEER_LOSS_INTERLOCK_E2E_PASS" in driver and
           "process_restart_blocked=1" in driver and
           "real_hardware_writes=0" in driver,
           "NOT_VERIFIED",
           "A0 real restarted-Master probe/loopback-only status has not completed successfully")

    nodes = {}
    for role in ("master", "slave"):
        db = root / role / "host-peer-interlock.sqlite"
        demand(single(sql(db, "PRAGMA integrity_check"), f"{role} SQLite integrity")[0] == "ok",
               "CORRUPT_INTERLOCK_WAL", role)
        rows = sql(db, "SELECT singleton, latched_utc, reason_code, detail FROM host_peer_interlock")
        singleton, stamped, reason, detail = single(rows, role + " persistent safety latch")
        demand(singleton == 1 and reason in
               ("PEER_LINK_LOST", "PEER_SEND_FAILED", "PEER_INBOUND_FAILED") and bool(detail),
               "INTERLOCK_INVALID", f"{role} missing valid cause and unique immutable latch")
        nodes[role] = dict(latchedUtc=iso(stamped), reason=reason)
        file = root / (role + ".log")
        demand(file.is_file(), "NOT_VERIFIED", f"{role} original Core log absent")
        log = file.read_text(encoding="utf8", errors="replace")
        stop = f"SIM_FULL_{role.upper()}_PEER_INTERLOCK_HOLD"
        reconnected = f"SIM_FULL_{role.upper()}_PEER_READONLY_RECONNECTED"
        demand(stop in log and reconnected in log and
               log.index(stop) < log.index(reconnected),
               "NOT_VERIFIED",
               f"{role} must durably latch BEFORE metadata-only PeerLink recovery")
        items = sql(root / role / "production.db",
                    "SELECT workpiece_id, final_json FROM workpieces")
        demand(bool(items) and len({workpiece for workpiece, _ in items}) == len(items),
               "WORKPIECE_LINEAGE", f"{role} missing/duplicated durable workpiece identity")
        demand(all(result is None for _, result in items),
               "FALSE_FINAL_AFTER_DISCONNECT",
               f"{role} has final result even though PeerLink is uncertain")
        nodes[role]["workpieces"] = {i for i, _ in items}

    demand(nodes["slave"]["workpieces"].issubset(nodes["master"]["workpieces"]),
           "CROSS_PEER_IDENTITY", "Slave registered an item Master never owned")
    phase_rows = sql(root / "master" / "sim-full-cycle.db",
                     "SELECT token,phase FROM sim_full_tokens")
    demand(bool(phase_rows) and all(phase != "exited" for _, phase in phase_rows),
           "UNSAFE_FIFO_EXIT", "Peer loss permitted completed physical ejection")

    wire_path = root / "p0-fins-wire.jsonl"
    wire = parse_trace(load_records(wire_path))  # independently parse RAW headers and replies
    demand(bool([w for w in wire if w.address in (4001, 1999, 4302)]),
           "NOT_VERIFIED", "No real FINS loading/register packets in supposed five-process run")
    illegal = [(w.seq, w.sid, w.address, w.words) for w in wire
               if w.write and w.address in (1016, 2099, 4003)]
    demand(not illegal, "UNSAFE_REPLAY_FINS_WRITE",
           f"Direct FINS packet decoder saw forbidden final/ejection writes: {illegal[:3]}")
    return {
        "status": "PASS_PEER_BREAK_READONLY_RECONNECT_AND_RESTART_GUARD",
        "five_distinct_processes": True, "master_latch": nodes["master"]["reason"],
        "slave_latch": nodes["slave"]["reason"],
        "slave_registered_subset_master": True,
        "real_fins_packets_checked": len(wire), "unacknowledged_final_writes_replayed": 0,
        "final_or_fifo_exit": 0,
        "real_master_restart_blocked_by_A0_driver": True,
        "no_automatic_PLC_replay": True,
        "sim_only": True, "field_verified": False
    }


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--evidence-root", type=Path, required=True)
    p.add_argument("--driver-log", type=Path, required=True)
    p.add_argument("--report", type=Path)
    a = p.parse_args(argv)
    try:
        report = check_evidence(a.evidence_root.resolve(), a.driver_log.resolve())
        rc = 0
        print("A5_PEER_RESTART_WIRE_PASS " + json.dumps(report), flush=True)
    except AuditFailure as exc:
        rc = 3 if exc.code == "NOT_VERIFIED" else 1
        report = {"status": "NOT_VERIFIED" if rc == 3 else "FAIL",
                  "reason": exc.code, "detail": str(exc)}
        print("A5_PEER_RESTART_WIRE_" + report["status"] + " " +
              json.dumps(report, ensure_ascii=False), file=sys.stderr)
    except (OSError, ValueError, KeyError, sqlite3.Error, TypeError) as exc:
        rc = 1
        report = {"status": "FAIL", "reason": "EVIDENCE_PARSE",
                  "detail": str(exc)}
        print("A5_PEER_RESTART_WIRE_FAIL " + json.dumps(report), file=sys.stderr)
    if a.report:
        a.report.parent.mkdir(parents=True, exist_ok=True)
        a.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n",
                            encoding="utf8")
    return rc


if __name__ == "__main__":
    raise SystemExit(main())
