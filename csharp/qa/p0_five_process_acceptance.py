#!/usr/bin/env python3
"""Agent05 full five-process P0 acceptance; never substitutes a fake PLC or Core.

Launches the repository's EXISTING SIM_FULL five-process runner. Requires genuine
A2 vPLC wire-hex export and A7 Core event journals from the very same data root.
Old D4000 pre-capture flow returns NOT_VERIFIED(3), even if its demo E2E passed.
"""
import argparse
import json
import os
from pathlib import Path
import re
import sqlite3
import subprocess
import sys
import tempfile
import time

from p0_fins_timeline_audit import AuditFailure, demand, load_records, value, verify_evidence


def from_ledger(root: Path, count: int):
    path = root / "master" / "sim-full-cycle.db"
    demand(path.is_file(), "NOT_VERIFIED", "Real Master token/identity SQLite missing")
    with sqlite3.connect(f"file:{path.as_posix()}?mode=ro", uri=True, timeout=5) as db:
        rows = db.execute(
            "SELECT token, identity_json FROM sim_full_tokens ORDER BY token").fetchall()
    demand(len(rows) == count, "WORKPIECE_COUNT_MISMATCH",
           f"Actual Master WAL token count {len(rows)} != requested {count}")
    items = []
    for token, identity_text in rows:
        ident = json.loads(identity_text)
        workpiece = value(ident, "workpieceId")
        demand(isinstance(token, int) and token > 0 and workpiece,
               "MISSING_IDENTITY", f"Token {token} lacks immutable WorkpieceId")
        items.append((token, ident))
    demand(len({value(identity, "workpieceId") for _, identity in items}) == count,
           "CROSS_WORKPIECE_IDENTITY", "Duplicate WorkpieceId across SIM_FULL pieces")
    return items


def export_timeline(cli: Path, dotnet: str, root: Path, workpiece: str,
                    target: Path, timeout: float):
    demand(cli.is_file(), "NOT_VERIFIED", "A7 Diagnostics CLI with timeline is not built")
    proc = subprocess.run(
        [dotnet, str(cli), "timeline", str(root), "--workpiece", workpiece,
         "--json", str(target)],
        cwd=cli.parent, capture_output=True, text=True, timeout=timeout,
        env={**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT": "1"})
    # A7 exit 3 means an already flagged causal error; do not invent a pass.
    demand(proc.returncode == 0 and target.is_file(),
           "NOT_VERIFIED" if proc.returncode == 2 else "CAUSAL_TIMELINE_CLI_ERROR",
           f"A7 timeline for {workpiece} exit={proc.returncode}: {proc.stderr[-350:]}")
    return load_records(target, "events")


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--evidence-root", type=Path, required=True)
    p.add_argument("--count", type=int, default=5)
    p.add_argument("--ordering", choices=["FRAME_FIRST", "REQUEST_FIRST"], required=True)
    p.add_argument("--expected-status", choices=["OK", "NG"], default="NG")
    p.add_argument("--missing-token", type=int, default=0)
    p.add_argument("--verify-final", action="store_true")
    p.add_argument("--repeat-barcode", action="store_true")
    p.add_argument("--dotnet", default="dotnet")
    p.add_argument("--timeout", type=int, default=110)
    p.add_argument("--audit-existing", action="store_true",
                   help="Audit existing five-process evidence without starting/restarting it")
    p.add_argument("--wire", type=Path, help="A2 actual wire export; default root/p0-fins-wire.jsonl")
    a = p.parse_args(argv)
    root = a.evidence_root.resolve()
    source = Path(__file__).resolve().parent.parent
    wire_path = a.wire.resolve() if a.wire else root / "p0-fins-wire.jsonl"
    report = root / "p0-a5-acceptance.json"
    try:
        demand(a.count >= 5 and a.count <= 40 and
               (a.missing_token == 0 or 1 <= a.missing_token <= a.count),
               "INVALID_INPUT", "Require 5..40 workpieces and an in-range missing token")
        if not a.audit_existing:
            demand(not root.exists() or not any(root.iterdir()),
                   "NONEMPTY_DATA_ROOT", "Cannot reuse existing production WAL/evidence root")
            root.mkdir(parents=True, exist_ok=True)
            env = dict(os.environ)
            env.update({
                "AOI_SIM_PLC_PROFILE": "LEGACY_PLC_SHADOW",
                "AOI_SIM_CAPTURE_ORDER": a.ordering,
                "AOI_SIM_FINS_WIRE_JSONL": str(wire_path),
                "AOI_SIM_MISSING_FRAME_TOKEN": str(a.missing_token),
                "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
            })
            args = [sys.executable, str(source / "scripts/e2e_sim_full.py"),
                    "--count", str(a.count), "--with-images",
                    "--evidence-root", str(root), "--timeout", str(a.timeout),
                    "--dotnet", a.dotnet]
            if a.repeat_barcode:
                args.append("--repeat-barcode")
            # This existing script launches actual vPLC, Master/Slave Core and
            # two UI OS processes, validates SQL/WAL/PNG and cleans up PIDs.
            # e2e_sim_full.py correctly REFUSES a nonempty evidence root.
            # Keep this launcher log outside that root until the real child
            # process run finishes; do not contaminate its clean-start WAL.
            with tempfile.TemporaryFile(mode="w+t", encoding="utf-8") as log:
                run = subprocess.run(args, cwd=source, env=env, stdout=log,
                                     stderr=subprocess.STDOUT, timeout=a.timeout + 40)
                log.seek(0)
                output = log.read()
            (root / "a5-five-process-driver.log").write_text(output, encoding="utf-8")
            demand("SIM_FULL_FIVE_PROCESSES_LAUNCHED" in output and
                   len(re.findall(r"SIM_FULL_PROCESS_STARTED name=", output)) == 5,
                   "NOT_VERIFIED", "Five genuine AOI child process launches not proven")
            demand(run.returncode == 0,
                   "LEGACY_BUSINESS_E2E_FAILED",
                   f"Existing five-process scenario failed, code={run.returncode}; see driver.log")
        items = from_ledger(root, a.count)
        demand(wire_path.is_file(), "NOT_VERIFIED",
               "A0 not yet exporting A2 actual LegacyWireTransaction raw UDP bytes: " +
               str(wire_path) + " (legacy D4003 memory mirror is insufficient)")
        wire = load_records(wire_path, "transactions")
        diagnostics = source / "src/AOI.Diagnostics.Cli/bin/Debug/net10.0/AOI.Diagnostics.Cli.dll"
        all_events, cases = [], []
        for token, identity in items:
            item = value(identity, "workpieceId")
            path = root / f"p0-a7-timeline-{token:03}.json"
            events = export_timeline(diagnostics, a.dotnet, root, item, path, a.timeout)
            all_events.extend(events)
            station = [e for e in events
                       if value(e, "stage") == "PlcCaptureRequestObserved"
                       and value(e, "plcDmAddress") == "D1011"
                       and value(e, "physicalDeviceId") == "HIK-M-2"]
            demand(len(station) == 1, "NOT_VERIFIED",
                   f"Token {token} did not emit ONE actual P0 ST05 PLC request / A1 capture")
            capture = value(station[0], "captureId")
            demand(bool(capture), "NOT_VERIFIED", "A1 assigned no capture ID")
            expected = "MISSING" if token == a.missing_token else a.expected_status
            spec = {"token": token, "workpieceId": item, "captureId": capture,
                    "epoch": value(identity, "epoch"),
                    "releaseId": value(identity, "releaseId"),
                    "traceId": value(identity, "traceId"),
                    "ordering": a.ordering, "result": expected,
                    "cameraAddress": 1011, "physicalDeviceId": "HIK-M-2",
                    "channelId": "CH06", "attemptNo": 1}
            if a.verify_final and expected != "MISSING":
                spec["scenario"] = "NORMAL_NG" if expected == "NG" else "NORMAL_GOOD"
            cases.append(spec)
        result = verify_evidence(wire, all_events, cases)
        result["actual_five_processes"] = True
        result["repeat_barcode"] = bool(a.repeat_barcode)
        status = 0
        print("A5_P0_FIVE_PROCESS_PASS " + json.dumps(result, separators=(",", ":")), flush=True)
    except AuditFailure as exc:
        status = 3 if exc.code == "NOT_VERIFIED" else 1
        result = {"status": "NOT_VERIFIED" if status == 3 else "FAIL",
                  "reason": exc.code, "detail": str(exc), "actual_p0_verified": False}
        print("A5_P0_FIVE_PROCESS_" + result["status"] + " " +
              json.dumps(result, ensure_ascii=False), file=sys.stderr, flush=True)
    except (OSError, ValueError, KeyError, TypeError, TimeoutError,
            sqlite3.Error, subprocess.TimeoutExpired) as exc:
        status = 1
        result = {"status": "FAIL", "reason": "E2E_RUNTIME",
                  "detail": str(exc), "actual_p0_verified": False}
        print("A5_P0_FIVE_PROCESS_FAIL " + json.dumps(result), file=sys.stderr, flush=True)
    if root.is_dir():
        report.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n",
                          encoding="utf-8")
    return status


if __name__ == "__main__":
    raise SystemExit(main())
