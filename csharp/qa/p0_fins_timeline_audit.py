#!/usr/bin/env python3
"""Agent05 independent P0 audit: RAW FINS UDP request/reply + A7 causal events.

This tool does not generate a frame, ACK, inspection or quality result. It ONLY
accepts actual wire exchanges exported by A2's LegacyPlcShadow.WireHistory and
actual Core timing events exported by A7. Legacy D4003 mirror writes cannot pass.
Missing evidence -> exit 3, malformed/incorrect evidence -> exit 1, pass -> 0.
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import pathlib
import struct
import sys
from dataclasses import dataclass


class AuditFailure(Exception):
    def __init__(self, code: str, detail: str):
        self.code = code
        super().__init__(detail)


def demand(ok: bool, code: str, detail: str):
    if not ok:
        raise AuditFailure(code, detail)


def value(row: dict, name: str, default=None):
    return row.get(name, row.get(name[0].upper() + name[1:], default))


def iso(value_: str) -> dt.datetime:
    demand(isinstance(value_, str) and bool(value_), "MISSING_TIMESTAMP",
           "Actual UTC receive timestamp is required")
    try:
        stamp = dt.datetime.fromisoformat(value_.replace("Z", "+00:00"))
    except ValueError as exc:
        raise AuditFailure("INVALID_TIMESTAMP", str(exc)) from exc
    demand(stamp.tzinfo is not None, "NON_UTC_TIMESTAMP", str(value_))
    return stamp.astimezone(dt.timezone.utc)


def unhex(raw, name):
    demand(isinstance(raw, str), "NO_RAW_PACKET", name + " must be raw FINS hex")
    try:
        return bytes.fromhex(raw)
    except ValueError as exc:
        raise AuditFailure("INVALID_HEX", name) from exc


@dataclass(frozen=True)
class Wire:
    seq: int
    token: int
    tick: int
    utc: dt.datetime
    sid: int
    address: int
    write: bool
    words: tuple[int, ...]
    reply_words: tuple[int, ...]
    end_code: int | None
    response_seen: bool
    outcome: str

    @property
    def ok(self) -> bool:
        return self.response_seen and self.end_code == 0 and self.outcome == "OK"


def parse_wire(row: dict) -> Wire:
    """Validate the actual FINS headers, swapped routing, SID, DM and end-code.

    Never use the untrusted Address/Values/Outcome metadata as packet proof.
    A readback MUST be observed in response bytes, not a PLC memory mirror.
    """
    req = unhex(value(row, "requestHex"), "requestHex")
    rsp = unhex(value(row, "responseHex"), "responseHex")
    demand(len(req) >= 18 and req[0] == 0x80 and req[1] == 0
           and req[2] == 2 and req[10] == 1 and req[11] in (1, 2)
           and req[12] == 0x82 and req[15] == 0,
           "MALFORMED_FINS_REQUEST", "FINS DM protocol header/command invalid")
    address = struct.unpack_from(">H", req, 13)[0]
    count = struct.unpack_from(">H", req, 16)[0]
    write = req[11] == 2
    demand(1 <= count <= 64 and address + count <= 5000
           and len(req) == 18 + (2 * count if write else 0),
           "MALFORMED_FINS_LENGTH", f"D{address} count={count}")
    words = tuple(struct.unpack_from(">" + "H" * count, req, 18)) if write else ()
    sid = req[9]
    demand(req[4] != 0 and req[7] != 0 and req[4] != req[7],
           "FINS_ROUTE_UNKNOWN", "Destination and source must be distinct, nonzero")
    end_code = None
    readback = ()
    if rsp:
        demand(len(rsp) >= 14 and rsp[0] == 0xC0 and rsp[1:3] == req[1:3]
               and rsp[3:6] == req[6:9] and rsp[6:9] == req[3:6]
               and rsp[9:12] == req[9:12],
               "FINS_RESPONSE_MISMATCH", f"D{address} SID={sid}: route, SID or cmd")
        end_code = struct.unpack_from(">H", rsp, 12)[0]
        demand(len(rsp) == (14 if write or end_code else 14 + 2 * count),
               "FINS_RESPONSE_LENGTH", f"D{address} SID={sid}")
        if not write and end_code == 0:
            readback = tuple(struct.unpack_from(">" + "H" * count, rsp, 14))
    demand(value(row, "address") == address and value(row, "sid") == sid
           and value(row, "isWrite") == write,
           "FINS_METADATA_MISMATCH", f"Recorded header metadata != raw packet D{address}")
    demand(tuple(value(row, "values", [])) == words,
           "FINS_VALUE_MISMATCH", f"D{address} claimed write differs from datagram")
    outcome = value(row, "outcome", "")
    demand(bool(outcome), "MISSING_OUTCOME", f"D{address} missing server outcome")
    if outcome == "OK":
        demand(end_code == 0, "FALSE_SUCCESS", f"D{address} no verified FINS success reply")
    if end_code not in (None, 0):
        demand(outcome != "OK", "FALSE_SUCCESS", f"D{address} returned rejection end code")
    seq = value(row, "sequence")
    tick = value(row, "monotonicTimestamp")
    token = value(row, "token")
    demand(type(seq) is int and seq > 0 and type(tick) is int and tick > 0
           and type(token) is int and 0 <= token <= 65535,
           "WIRE_IDENTITY_MISSING", "A2 sequence, token and monotonic ticks required")
    return Wire(seq, token, tick, iso(value(row, "receivedUtc")),
                sid, address, write, words, readback, end_code, bool(rsp), outcome)


def load_records(path: pathlib.Path, member: str | None = None) -> list[dict]:
    demand(path.is_file(), "NOT_VERIFIED", f"Actual evidence missing: {path}")
    text = path.read_text(encoding="utf-8-sig").strip()
    demand(bool(text), "NOT_VERIFIED", f"Empty evidence: {path}")
    try:
        parsed = json.loads(text)
        if isinstance(parsed, dict):
            parsed = parsed.get(member or "", parsed.get("events", parsed.get("transactions")))
        demand(isinstance(parsed, list), "NOT_VERIFIED", f"{path}: expected JSON array")
        return parsed
    except json.JSONDecodeError:
        try:
            return [json.loads(line) for line in text.splitlines() if line.strip()]
        except json.JSONDecodeError as exc:
            raise AuditFailure("BAD_EVIDENCE_JSON", str(exc)) from exc


def parse_trace(records: list[dict]) -> list[Wire]:
    demand(bool(records), "NOT_VERIFIED", "A2 actual FINS transaction capture is empty")
    wire = [parse_wire(row) for row in records]
    for left, right in zip(wire, wire[1:]):
        demand(right.seq == left.seq + 1 and right.tick >= left.tick
               and right.utc >= left.utc,
               "WIRE_ORDER_UNTRUSTED", "Sequence, monotonic or UTC went backward")
    return wire


def one(rows: list, label: str):
    demand(len(rows) == 1, "MISSING_OR_DUPLICATE",
           f"{label}: expected ONE observed operation, got {len(rows)}")
    return rows[0]


def verify_station_wire(wire: list[Wire], spec: dict):
    token = spec["token"]
    addr = spec.get("cameraAddress", 1011)
    ack = 3 if spec["result"] == "MISSING" else 2
    offered = one([w for w in wire if w.token == token and w.address == 4300
                   and w.write and w.words == (token, addr) and w.ok],
                  f"token {token} private external capture offer")
    sent = one([w for w in wire if w.token == token and w.address == addr
                and w.write and w.ok and w.words == (ack,)],
               f"token {token} D{addr} CamDone={ack} reply")
    # Core may poll D1011=1 multiple times while awaiting the hard frame.
    # Repeated READS are legal; duplicate CamDone WRITES are not.
    requests = [w for w in wire if w.token == token and w.address == addr
                and not w.write and w.ok and w.reply_words == (1,)
                and offered.seq < w.seq < sent.seq]
    demand(bool(requests), "MISSING_OR_DUPLICATE",
           f"token {token} no actual PLC D{addr}=1 observed before CamDone")
    reads_after = [w for w in wire if w.token == token and w.address == addr
                   and not w.write and w.seq > sent.seq]
    demand(bool(reads_after) and reads_after[0].ok
           and reads_after[0].reply_words == (ack,),
           "CAPTURE_READBACK_MISMATCH",
           f"token {token} first FINS D{addr} read after CamDone is not {ack}")
    demand(requests[0].seq > offered.seq,
           "CAPTURE_WIRE_ORDER", "External offer/request is out of order")
    competing = [w for w in wire if w.token == token and w.address == addr and
                 w.write and w is not sent]
    demand(not competing, "DUPLICATE_CAM_DONE",
           f"D{addr} has an extra, rejected or unacknowledged CamDone write")
    return sent


def verify_final_wire(wire: list[Wire], spec: dict):
    token = spec["token"]
    scenario = spec.get("scenario")
    if scenario is None:
        return []
    desired = {
        "NORMAL_GOOD": ((1016, 1), (2099, 2)),
        "NORMAL_NG": ((1016, 2), (2099, 2)),
        "SCAN_FAILED": ((2099, 3), (1016, 2)),
        "BARCODE_MISMATCH": ((2099, 4), (1016, 2)),
    }
    demand(scenario in desired, "UNKNOWN_SCENARIO", str(scenario))
    expected = desired[scenario]
    offer_value = 0 if scenario.startswith("NORMAL") else (1 if scenario == "SCAN_FAILED" else 2)
    offer = one([w for w in wire if w.token == token and w.address == 4303
                 and w.write and w.words == (token, offer_value) and w.ok],
                f"token {token} end scanner private offer")
    # The end-scan request may be polled repeatedly; require at least one
    # verified D2099=1 before either final write, not exactly one read.
    ready = [w for w in wire if w.token == token and w.address == 2099
             and not w.write and w.ok and w.reply_words == (1,)]
    all_writes = [w for w in wire if w.token == token and w.write
                  and w.address in (1016, 2099)]
    demand(len(all_writes) == 2 and all(x.ok for x in all_writes),
           "INCOMPLETE_FINAL_WRITE",
           f"token {token}: every final write must have a successful actual FINS response; never assume a timeout was not applied")
    writes = all_writes
    demand([(w.address, *w.words) for w in writes] == list(expected),
           "FINAL_WIRE_ORDER", f"token {token}: actual {[(w.address, w.words) for w in writes]}, expected {expected}")
    demand(bool(ready) and offer.seq < ready[0].seq < writes[0].seq < writes[1].seq,
           "FINAL_SEQUENCE", f"token {token} result written without valid end request")
    for i, w in enumerate(writes):
        next_write = writes[i + 1].seq if i + 1 < len(writes) else 2 ** 62
        reads = [x for x in wire if x.token == token and not x.write
                 and x.address == w.address and w.seq < x.seq < next_write]
        demand(bool(reads) and reads[0].ok and reads[0].reply_words == w.words,
               "FINAL_READBACK_MISMATCH",
               f"token {token} first FINS D{w.address} readback not equal to acknowledged write")
        demand(reads[0].utc >= w.utc, "FINAL_ACK_TIME", "Readback preceded write")
    return writes


def event_value(ev: dict, key: str, default=None):
    return ev.get(key, ev.get(key[0].upper() + key[1:], default))


def verify_timeline(events: list[dict], spec: dict, ack_wire: Wire):
    token = spec["token"]
    item = spec["workpieceId"]
    capture = spec["captureId"]
    addr = spec.get("cameraAddress", 1011)
    device = spec.get("physicalDeviceId", "HIK-M-2")
    channel = spec.get("channelId", "CH06")
    channels = spec.get("channelIds", [channel])
    known = {1011: ("HIK-M-2", ["CH06"]),
             1013: ("LINE-S-8", ["CH08", "CH09"]),
             1014: ("LINE-M-2", ["CH10", "CH11"])}
    demand(addr in known and device == known[addr][0]
           and channels == known[addr][1],
           "INVALID_CAMERA_PROFILE", f"Unverified capture mapping D{addr} / {device}: {channels}")
    matching = [e for e in events if event_value(e, "workpieceId") == item
                and event_value(e, "captureId") == capture]
    demand(bool(matching), "NOT_VERIFIED",
           f"token {token}: no actual Core event for WorkpieceId/CaptureId")
    demand(spec.get("epoch") is not None and bool(spec.get("releaseId"))
           and bool(spec.get("traceId")),
           "NOT_VERIFIED", "Expected immutable Epoch/ReleaseId/TraceId must come from Core WAL")
    for e in matching:
        demand(event_value(e, "physicalDeviceId") == device
               and event_value(e, "attemptNo") == spec.get("attemptNo", 1)
               and event_value(e, "epoch") == spec["epoch"]
               and event_value(e, "releaseId") == spec["releaseId"]
               and event_value(e, "traceId") == spec["traceId"],
               "FRAME_IDENTITY_MISMATCH",
               "Capture/Trace/Epoch/Release identity differs from Core's immutable WorkpieceIdentity")
    def stage(name):
        return [x for x in matching if event_value(x, "stage") == name]
    request = one(stage("PlcCaptureRequestObserved"), "actual PLC capture request")
    demand(event_value(request, "plcDmAddress") == f"D{addr}"
           and event_value(request, "plcValue") == 1,
           "PLC_REQUEST_MISMATCH", "Observed PLC request must be D1011=1")
    acknowledged = one(stage("CamDoneAcknowledged"), "FINS-confirmed CamDone")
    demand(event_value(acknowledged, "plcDmAddress") == f"D{addr}"
           and event_value(acknowledged, "plcValue") ==
           (3 if spec["result"] == "MISSING" else 2)
           and event_value(acknowledged, "finsSid") == ack_wire.sid,
           "CAM_DONE_NOT_WIRE_LINKED", "A7 ACK SID, DM and value must match real FINS packet")
    # Strict same-process monotonic comparison. Across machines/boots these
    # ticks cannot establish order; never silently fall back to cross-node UTC.
    def tick(e):
        t = event_value(e, "monotonicTick")
        p = event_value(e, "processRunId")
        demand(isinstance(t, int) and t > 0 and bool(p), "NOT_VERIFIED",
               "Missing actual process-local monotonic timestamp")
        return p, t
    def before(a, b, message):
        p, t = tick(a)
        q, u = tick(b)
        demand(p == q, "NOT_VERIFIED", "Cross-process monotonic order is not comparable")
        demand(t < u, "CAUSAL_ORDER", message)
    trigger = one(stage("HardwareTriggerObserved"), "actual external camera trigger")
    if spec["result"] == "MISSING":
        timeout = one(stage("CaptureTimedOut"), "single-workpiece frame timeout")
        demand(not stage("FrameReady") and not stage("InspectionCompleted"),
               "FALSE_GOOD_ON_MISSING", "Missing image must not be inspected or treated as OK")
        before(trigger, timeout, "Missing frame timeout was recorded before hard trigger")
        before(request, timeout, "Timeout occurred before PLC request")
        before(timeout, acknowledged, "CamDone=3 before documented timeout")
        return
    arrived = one(stage("FrameArrived"), "hard-trigger frame arrival")
    before(trigger, arrived, "Frame arrived before the actual hardware trigger")
    ready = one(stage("FrameReady"), "validated FrameReady")
    inspected = stage("InspectionCompleted")
    demand(len(inspected) == len(channels) and
           sorted(event_value(e, "channelId") for e in inspected) == sorted(channels),
           "CHANNEL_FANOUT_MISMATCH",
           f"One physical D{addr} capture must produce exactly {channels} once each")
    frame = event_value(arrived, "frameId")
    demand(bool(frame) and frame == event_value(ready, "frameId"),
           "FRAME_IDENTITY_MISMATCH", "FrameReady used a different physical FrameId")
    child_frames = []
    for result in inspected:
        child = event_value(result, "frameId")
        demand(bool(child) and event_value(result, "status") in ("OK", "NG"),
               "ALGORITHM_STATUS_INVALID", "Inspection must have real FrameId and OK/NG result")
        if len(channels) == 1:
            demand(child == frame, "FRAME_IDENTITY_MISMATCH",
                   "Area inspection must preserve the acquired FrameId")
        else:
            # A3 persists each line-scan child with its own FrameId and the
            # SAME real acquired parent frame in A7 FromResult.Details.
            details = str(event_value(result, "details", ""))
            demand(child != frame and f"parentFrame={frame}".lower() in details.lower(),
                   "LINE_PARENT_FRAME_MISMATCH",
                   "Interleaved line result must retain real common parentFrame")
            child_frames.append(child)
    if len(channels) == 2:
        demand(len(set(child_frames)) == 2, "DUPLICATE_LINE_CHILD",
               "Two logical inspection channels cannot reuse one child frame")
    order = spec["ordering"]
    demand(order in ("FRAME_FIRST", "REQUEST_FIRST"), "UNKNOWN_ORDERING", str(order))
    if order == "FRAME_FIRST":
        before(arrived, request, "Frame-first actually arrived after PLC request")
    else:
        before(request, arrived, "Request-first actually arrived after frame")
    before(arrived, ready, "FrameReady before FrameArrived")
    before(ready, acknowledged, "CamDone acknowledged before FrameReady")
    before(request, acknowledged, "CamDone acknowledged before PLC request")
    for result in inspected:
        before(acknowledged, result, "Inspection result preceded confirmed CamDone")


def verify_evidence(wire_records: list[dict], events: list[dict], cases: list[dict]) -> dict:
    wire = parse_trace(wire_records)
    demand(bool(cases), "NOT_VERIFIED", "No workpieces specified for independent audit")
    seen_items, seen_capture, seen_tokens = set(), set(), set()
    # Independent of case iteration: a frame (area, line parent or child)
    # can be consumed only by the capture that originally acquired it.
    frame_owner = {}
    for e in events:
        if event_value(e, "stage") not in ("FrameArrived", "FrameReady", "InspectionCompleted"):
            continue
        frame = event_value(e, "frameId")
        if not frame:
            continue
        owner = (event_value(e, "workpieceId"), event_value(e, "captureId"))
        demand(owner[0] and owner[1], "FRAME_IDENTITY_MISMATCH",
               "Real FrameId lacking immutable WorkpieceId/CaptureId")
        demand(frame not in frame_owner or frame_owner[frame] == owner,
               "CROSS_WORKPIECE_FRAME_REUSE",
               f"FrameId={frame} observed in two distinct captures")
        frame_owner[frame] = owner
    for spec in cases:
        token = spec["token"]
        item = spec["workpieceId"]
        capture = spec["captureId"]
        demand(token not in seen_tokens and item not in seen_items
               and capture not in seen_capture,
               "WORKPIECE_REUSED", "Token, WorkpieceId and CaptureId must be unique")
        seen_tokens.add(token)
        seen_items.add(item)
        seen_capture.add(capture)
        ack = verify_station_wire(wire, spec)
        verify_timeline(events, spec, ack)
        verify_final_wire(wire, spec)
    return {"status": "PASS_REAL_FINS_AND_CAUSAL_EVENTS", "workpieces": len(cases),
            "wire_exchanges": len(wire), "physical_capture_ack": len(cases),
            "end_sequences": sum(bool(x.get("scenario")) for x in cases),
            "raw_packets_verified": True, "field_verified": False}


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--wire", type=pathlib.Path, required=True,
                   help="A2 real LegacyWireTransaction JSON array or JSONL; raw hex is mandatory")
    p.add_argument("--timeline", type=pathlib.Path, required=True,
                   help="A7 actual TimingTimeline export JSON; not reconstructed from Core final values")
    p.add_argument("--cases", type=pathlib.Path, required=True,
                   help="JSON list of WorkpieceId/CaptureId/token/order/result and optional end scenario")
    p.add_argument("--report", type=pathlib.Path)
    a = p.parse_args(argv)
    try:
        wire = load_records(a.wire, "transactions")
        events = load_records(a.timeline, "events")
        cases = load_records(a.cases, "cases")
        result = verify_evidence(wire, events, cases)
        rc = 0
        print("A5_P0_FINS_TIMELINE_PASS " + json.dumps(result, separators=(",", ":")), flush=True)
    except AuditFailure as exc:
        rc = 3 if exc.code == "NOT_VERIFIED" else 1
        result = {"status": "NOT_VERIFIED" if rc == 3 else "FAIL",
                  "reason": exc.code, "detail": str(exc),
                  "field_verified": False}
        print("A5_P0_FINS_TIMELINE_" + result["status"] + " " + json.dumps(result), file=sys.stderr)
    except (ValueError, KeyError, TypeError, OSError) as exc:
        rc = 1
        result = {"status": "FAIL", "reason": "INVALID_INPUT", "detail": str(exc)}
        print("A5_P0_FINS_TIMELINE_FAIL " + json.dumps(result), file=sys.stderr)
    if a.report:
        a.report.parent.mkdir(parents=True, exist_ok=True)
        a.report.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    return rc


if __name__ == "__main__":
    raise SystemExit(main())
