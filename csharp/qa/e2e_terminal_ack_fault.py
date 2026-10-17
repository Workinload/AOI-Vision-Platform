#!/usr/bin/env python3
"""A5 genuine five-process terminal FINS ACK-loss/rejection integration.

Uses current A0 Host/A2 legacy PLC shadow, WITHOUT a new PLC/Core mock.
A2 intentionally mutates the PLC after receiving a raw FINS WRITE but
discards its response. The same WorkpieceId must remain held; an unreadable
barcode is supplied exclusively by the vPLC, never forged inside Host.
A fresh READ-ONLY FINS/UDP query establishes post-fault PLC memory state;
raw packet history proves ordering/SID/no replay, never merely the end values.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import time

from e2e_sim_full import fins_read, make_png
from p0_fins_timeline_audit import AuditFailure, demand, load_records, parse_trace
from p0_terminal_wire_audit import decode_scan, sql


def check(root: Path, fault: str, scenario: str, require_utc=True):
    ledger = root / "master/sim-full-cycle.db"
    master = root / "master/production.db"
    slave = root / "slave/production.db"
    loaded = sql(ledger,
                 "SELECT token,identity_json,phase FROM sim_full_tokens ORDER BY token")
    demand(len(loaded) == 3 and [x[0] for x in loaded] == [1, 2, 3],
           "NOT_VERIFIED", "Not yet three registered physical workpieces")
    abnormal = json.loads(loaded[2][1])
    wp = abnormal["workpieceId"]
    reason = "END_BARCODE_MISMATCH" if scenario == "mismatch" else "END_BARCODE_SCAN_FAILED"
    holds = sql(master, "SELECT reason_code,raised_utc FROM workpiece_holds WHERE workpiece_id=?", (wp,))
    demand(bool(holds), "NOT_VERIFIED", "Faulty token not yet durably held")
    demand(len(holds) == 1 and holds[0][0] == reason and
           loaded[2][2] == "registered", "UNSAFE_HOLD", f"token3 incorrect HOLD: {holds}")
    demand(sql(master, "SELECT COUNT(*) FROM channel_results")[0][0] == 33 and
           sql(slave, "SELECT COUNT(*) FROM channel_results")[0][0] == 18,
           "NOT_VERIFIED", "Nine physical cameras / 11 logical channels not yet completed")
    demand(sql(master, "SELECT COUNT(*) FROM workpieces WHERE final_json IS NOT NULL")[0][0] == 2,
           "NOT_VERIFIED", "Healthy tokens not finished; faulty token must have NO final")
    demand(loaded[0][2] == loaded[1][2] == "exited",
           "NOT_VERIFIED", "Unaffected token1/2 not yet safely exited")
    demand(sql(master, "SELECT final_json FROM workpieces WHERE workpiece_id=?", (wp,))
           == [(None,)], "UNSAFE_FAULT_FINAL", "Faulty workpiece assigned Final result")

    wire = parse_trace(load_records(root / "p0-fins-wire.jsonl"))
    first_address = 2099
    second_address = 1016
    first_status = 4 if scenario == "mismatch" else 3
    all_fault_writes = [w for w in wire if w.token == 3 and w.write and
                        w.address in (1016, 2099)]
    target = ((2099, first_status),) if fault in ("ack-first", "reject-first") else (
        (2099, first_status), (1016, 2))
    demand(tuple((w.address, *w.words) for w in all_fault_writes) == target,
           "DUPLICATE_OR_WRONG_FINAL_WRITE",
           f"Expected {target}, actual {[(w.address, w.words) for w in all_fault_writes]}")
    first = all_fault_writes[0]
    demand(first.sid != 0 and first.seq > 0 and first.tick > 0,
           "INVALID_RAW_FINS_SID", "Real first SID/sequence missing")
    fault_write = all_fault_writes[0] if fault != "ack-second" else all_fault_writes[1]
    if fault in ("ack-first", "ack-second"):
        demand(fault_write.outcome == "INJECTED_TERMINAL_ACK_LOST_AFTER_APPLY"
               and not fault_write.response_seen and fault_write.end_code is None,
               "ACK_LOSS_NOT_OBSERVED",
               "Shadow must apply actual write BEFORE reply is lost, raw reply MUST be empty")
    else:
        demand(fault_write.outcome == "INJECTED_TERMINAL_WRITE_REJECTED"
               and fault_write.response_seen and fault_write.end_code == 5,
               "FINS_EXPLICIT_REFUSAL_NOT_OBSERVED",
               "PLC refusal must have real FINS endcode=0x0005")
    if fault == "ack-second":
        demand(first.ok and all_fault_writes[0].sid != all_fault_writes[1].sid,
               "FIRST_STEP_NOT_ACKNOWLEDGED",
               "Step2 cannot be attempted without verified earlier step1")
        readbacks = [w for w in wire if w.token == 3 and not w.write and
                     w.address == 2099 and w.seq > first.seq
                     and w.seq < all_fault_writes[1].seq]
        demand(bool(readbacks) and readbacks[0].ok and
               readbacks[0].reply_words == (first_status,),
               "FIRST_STEP_READBACK_MISSING",
               "First step must have actual independently ACKed readback")
    end_reads = [w for w in wire if w.token == 3 and
                 w.address == 2100 and not w.write and w.ok]
    demand(bool(end_reads) and len(end_reads[0].reply_words) == 10,
           "MISSING_EXTERNAL_SCANNER",
           "A0 must first read the real external PLC's D2100 ten barcode words")
    scanned = decode_scan(end_reads[0].reply_words)
    demand((bool(scanned) and scanned != abnormal["barcode"]) if scenario == "mismatch"
           else not scanned, "FALSE_SCANNER_FAULT",
           "Host-only fabricated scanner status cannot justify legacy abnormal write")
    if scenario == "unreadable":
        # A2 deliberately labels this legitimate successful FINS read
        # INJECTED_END_SCAN_UNREADABLE rather than OK. Verify the *actual*
        # response bytes/end-code, not the simulator's semantic label alone.
        source_reads = [w for w in wire if w.token == 3 and
                        w.address == 2020 and not w.write and
                        w.response_seen and w.end_code == 0 and
                        w.outcome == "INJECTED_END_SCAN_UNREADABLE" and
                        len(w.reply_words) == 10]
        demand(bool(source_reads) and decode_scan(source_reads[-1].reply_words) == "",
               "MISSING_EXTERNAL_D2020_FAULT",
               "Unreadable scanner MUST originate in A2 external PLC's D2020 FINS response")
    log = (root / "master.log").read_text(encoding="utf8", errors="replace")
    demand(f"SIM_LEGACY_ABNORMAL_OUTPUT_UNCERTAIN token=3" in log and
           "no_automatic_retry=1" in log,
           "NOT_VERIFIED", "Master had not surfaced the actual unknown ACK/failed reply")
    expected_confirmed = 1 if fault == "ack-second" else 0
    demand(f"confirmed_steps={expected_confirmed}" in log,
           "UNSAFE_CONFIRMED_STEPS",
           f"Master must expose exactly {expected_confirmed} independently confirmed steps")
    if require_utc:
        from p0_fins_timeline_audit import iso
        demand(iso(holds[0][1]) <= fault_write.utc,
               "HOLD_AFTER_FINS_SEND", "HOLD was not durable before real abnormal FINS WRITE")
    # No false GOOD nor false PLC release for the uncertain item.
    demand(not any(w.token == 3 and w.write and w.address == 1016 and
                   w.words == (1,) for w in wire),
           "FALSE_GOOD", "Missing/incorrect barcode workpiece sent FINS D1016=1")
    demand(not any(w.write and w.address == 4003 and
                   w.words and w.words[0] == 3 for w in wire),
           "FALSE_PHYSICAL_EJECT", "Faulty token3 was ejected")
    demand(not any(w.token > 3 and w.write and w.address in (1016, 2099) for w in wire),
           "OVERTAKEN_HOLD", "Later workpiece overran held token3")
    return len(wire), wp


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--evidence-root", type=Path)
    p.add_argument("--fault", choices=("ack-first", "ack-second", "reject-first"),
                   required=True)
    p.add_argument("--scenario", choices=("mismatch", "unreadable"),
                   default="mismatch")
    p.add_argument("--dotnet", default="dotnet")
    p.add_argument("--timeout", type=int, default=85)
    a = p.parse_args(argv)
    source = Path(__file__).resolve().parent.parent
    root = (a.evidence_root.resolve() if a.evidence_root else
            Path(tempfile.mkdtemp(prefix="a5-end-acklost-")))
    if root.exists() and any(root.iterdir()):
        raise RuntimeError("A5 five-process fault needs clean evidence folder")
    root.mkdir(parents=True, exist_ok=True)
    host = source / "src/AOI.Host/bin/Debug/net10.0/AOI.Host.dll"
    ui = source / "src/AOI.Ui.Avalonia/bin/Debug/net10.0/AOI.Ui.Avalonia.dll"
    if not host.is_file() or not ui.is_file():
        raise RuntimeError("Build .NET solution Debug before actual five-process check")

    image_root = root / "operator-images"
    for ch in range(1, 8):
        make_png(image_root / f"CH{ch:02d}.png", bright_defect=(ch == 6))
    make_png(image_root / "LINE-S-8.png", line_parent=True)
    make_png(image_root / "LINE-M-2.png", line_parent=True)
    env = dict(os.environ)
    env.update({
        "AOI_SIM_MODE": "SIM_FULL", "AOI_SIM_CHANNEL_SET": "ALL_11",
        "AOI_SIM_ITEM_COUNT": "3", "AOI_SIM_MAX_IN_FLIGHT": "3",
        "AOI_SIM_END_BARCODE_FLIP_TOKEN": "3" if a.scenario == "mismatch" else "0",
        "AOI_SIM_END_SCAN_FAIL_TOKENS": "3" if a.scenario == "unreadable" else "",
        "AOI_SIM_PLC_PROFILE": "LEGACY_PLC_SHADOW",
        "AOI_SIM_ST05_ORDER": "REQUEST_FIRST",
        "AOI_SIM_IMAGE_ROOT": str(image_root), "AOI_SIM_IMAGE_LAYOUT": "FLAT",
        "AOI_SIM_CONFIG_TOKEN": "SIM_ONLY_E2E_ROI_OPERATOR_TOKEN_2026",
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "AOI_SIM_END_LOSE_ACK": ("3:1016" if a.fault == "ack-second" else "3:2099")
                                if a.fault != "reject-first" else "",
        "AOI_SIM_END_REJECT_WRITE": "3:2099" if a.fault == "reject-first" else "",
    })
    processes, handles = [], []
    def start(name, dll, *arguments):
        log = (root / (name + ".log")).open("w", encoding="utf8")
        handles.append(log)
        process = subprocess.Popen([a.dotnet, str(dll), *arguments], cwd=source,
                                   env=env, stdout=log, stderr=subprocess.STDOUT)
        processes.append((name, process))
        print(f"A5_REAL_OS_PROCESS name={name} pid={process.pid}", flush=True)

    try:
        start("vplc", host, "vplc", "--data", str(root))
        start("slave", host, "slave", "--data", str(root))
        start("master", host, "master", "--data", str(root))
        start("master-ui", ui, "--master", "--headless", "--once")
        start("slave-ui", ui, "--slave", "--headless", "--once")
        deadline = time.monotonic() + a.timeout
        last = "Not yet an observed five-process result"
        while time.monotonic() < deadline:
            for name, proc in processes[:3]:
                if proc.poll() is not None:
                    raise RuntimeError(f"{name} unexpectedly exited {proc.returncode}")
            try:
                count, item = check(root, a.fault, a.scenario)
                break
            except (AuditFailure, OSError, sqlite3.Error, ValueError, KeyError) as ex:
                last = str(ex)
            time.sleep(0.18)
        else:
            raise TimeoutError("P0 five-process fault not ready: " + last)

        for name, p_ui in processes[3:]:
            if p_ui.poll() is None:
                p_ui.wait(timeout=12)
            assert p_ui.returncode == 0, (name, "native UI IPC failed")
            assert "UI_HEADLESS_SNAPSHOT_OK" in (root / (name + ".log")).read_text(
                encoding="utf8"), (name, "real GUI client did not read Core IPC")

        # Stabilization interval: app must remain alive without retrying unknown
        # side effect when FINS first/second response vanished.
        time.sleep(0.8)
        after, _ = check(root, a.fault, a.scenario)
        assert after >= count
        # Probe actual PLC via a FRESH read-only FINS/UDP request, not a memory mirror.
        expect_2099 = 1 if a.fault == "reject-first" else (4 if a.scenario == "mismatch" else 3)
        expect_1016 = 2 if a.fault == "ack-second" else 0
        assert fins_read(2099) == expect_2099, "PLC after-applied first-word proof failed"
        assert fins_read(1016) == expect_1016, "PLC after-applied second-word proof failed"
        check(root, a.fault, a.scenario)

        # Restart the actual Master binary with the same production WAL. A0/A1
        # must refuse automatic replay; the original is stopped first.
        master_proc = processes[2][1]
        master_proc.terminate()
        master_proc.wait(timeout=6)
        restarted = subprocess.run(
            [a.dotnet, str(host), "master", "--data", str(root)],
            cwd=source, env=env, text=True, capture_output=True, timeout=12)
        (root / "master-restart-probe.log").write_text(
            restarted.stdout + "\n" + restarted.stderr, encoding="utf8")
        assert restarted.returncode != 0 and "SIM_FULL_RESTART_HOLD" in (
            restarted.stdout + restarted.stderr), (
            "Actual Master restarted with uncertain PLC write or did not log its restart HOLD")
        proof_wire = parse_trace(load_records(root / "p0-fins-wire.jsonl"))
        unsafe = [x for x in proof_wire if x.token == 3 and x.write and
                  x.address in (1016, 2099)]
        assert len(unsafe) == (2 if a.fault == "ack-second" else 1), (
            "Restart or reconnection sent duplicate final FINS datagram")
        result = dict(status="PASS_REAL_FIVE_PROCESS_TERMINAL_ACK_FAULT",
                      scenario=a.scenario, fault=a.fault, workpieceId=item,
                      packets=len(proof_wire), physicalFinalWrites=len(unsafe),
                      firstWrittenAfterLostAck=True,
                      actualReadOnlyFins2099=expect_2099,
                      actualReadOnlyFins1016=expect_1016,
                      noUnsafeFinsReplay=True, masterRestartBlocked=True,
                      slaveCoreSurvived=True, simulationOnly=True, fieldVerified=False)
        (root / "a5-terminal-fault-evidence.json").write_text(
            json.dumps(result, indent=2) + "\n", encoding="utf8")
        print("A5_REAL_FIVE_PROCESS_END_ACK_FAULT_PASS " +
              json.dumps(result, separators=(",", ":")), flush=True)
        return 0
    except Exception as error:
        print("A5_REAL_FIVE_PROCESS_END_ACK_FAULT_FAIL " + repr(error), file=sys.stderr)
        for name, _ in processes:
            path = root / (name + ".log")
            if path.is_file():
                print(name + ":\n" +
                      path.read_text(encoding="utf8", errors="replace")[-1600:],
                      file=sys.stderr)
        return 1
    finally:
        for name, proc in reversed(processes):
            if proc.poll() is None:
                proc.terminate()
        for _, proc in processes:
            try:
                proc.wait(timeout=4)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait()
        for h in handles:
            h.close()
        print("A5_TERMINAL_FAULT_EVIDENCE=" + str(root), flush=True)


if __name__ == "__main__":
    raise SystemExit(main())
