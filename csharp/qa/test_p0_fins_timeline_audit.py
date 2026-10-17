"""Unit fixtures for independent raw-wire audit; these are NOT Host E2E evidence.

The one UDP test uses a real loopback socket for parser transport sanity only.
The P0 CLI requires A2/A7 production-observed raw exchanges to pass.
"""
import datetime as dt
import json
import socket
import struct
import threading
import unittest
import uuid

from p0_fins_timeline_audit import AuditFailure, parse_trace, verify_evidence


def sample_packet(write, address, words, sid, reply_words=(), end_code=0):
    count = len(words) if write else len(reply_words)
    request = bytearray(18 + (2 * count if write else 0))
    request[:10] = bytes([0x80, 0, 2, 0, 50, 0, 0, 130, 0, sid])
    request[10:13] = bytes([1, 2 if write else 1, 0x82])
    struct.pack_into(">H", request, 13, address)
    struct.pack_into(">H", request, 16, count)
    if write:
        struct.pack_into(">" + "H" * count, request, 18, *words)
    response = bytearray(14 + (0 if write or end_code else 2 * count))
    response[:12] = request[:12]
    response[0] = 0xC0
    response[3:9] = request[6:9] + request[3:6]
    struct.pack_into(">H", response, 12, end_code)
    if not write and not end_code:
        struct.pack_into(">" + "H" * count, response, 14, *reply_words)
    return bytes(request), bytes(response)


def fixture(order="FRAME_FIRST", result="OK", scenario=None, token=1, line=False, repeat_poll=False):
    address = 1013 if line else 1011
    device = "LINE-S-8" if line else "HIK-M-2"
    channels = ["CH08", "CH09"] if line else ["CH06"]
    wire, sequence = [], 0
    def tx(address, write=False, words=(), reply=(), end=0):
        nonlocal sequence
        sequence += 1
        request, response = sample_packet(write, address, words, sequence, reply, end)
        item = dict(sequence=sequence, receivedUtc="2026-10-17T08:00:00+00:00",
                    monotonicTimestamp=sequence * 100, sid=sequence, isWrite=write,
                    address=address, values=list(words), token=token,
                    requestHex=request.hex(), responseHex=response.hex(),
                    outcome="OK" if not end else "REJECTED_TEST")
        wire.append(item)
        return item
    tx(4300, True, (token, address))
    tx(address, reply=(1,))
    if repeat_poll:
        tx(address, reply=(1,))
    ack = tx(address, True, (3 if result == "MISSING" else 2,))
    tx(address, reply=(3 if result == "MISSING" else 2,))
    if scenario is not None:
        mode = {"NORMAL_GOOD": 0, "NORMAL_NG": 0,
                "SCAN_FAILED": 1, "BARCODE_MISMATCH": 2}[scenario]
        tx(4303, True, (token, mode))
        tx(2099, reply=(1,))
        for final_address, value in {
            "NORMAL_GOOD": [(1016, 1), (2099, 2)],
            "NORMAL_NG": [(1016, 2), (2099, 2)],
            "SCAN_FAILED": [(2099, 3), (1016, 2)],
            "BARCODE_MISMATCH": [(2099, 4), (1016, 2)]
        }[scenario]:
            tx(final_address, True, (value,))
            tx(final_address, reply=(value,))
    workpiece = str(uuid.uuid4())
    capture = str(uuid.uuid4())
    frame = str(uuid.uuid4())
    process = str(uuid.uuid4())
    trace = str(uuid.uuid4())
    spec = dict(token=token, workpieceId=workpiece, captureId=capture,
                epoch=1, releaseId="SIM_TEST_RELEASE", traceId=trace,
                physicalDeviceId=device, channelId=channels[0],
                cameraAddress=address, ordering=order, result=result)
    if line:
        spec["channelIds"] = channels
    if scenario:
        spec["scenario"] = scenario
    events = []
    def event(stage, tick, **extra):
        payload = dict(workpieceId=workpiece, captureId=capture,
                       physicalDeviceId=device, attemptNo=1,
                       epoch=1, releaseId="SIM_TEST_RELEASE", traceId=trace,
                       processRunId=process, monotonicTick=tick,
                       stage=stage, **extra)
        events.append(payload)
    event("HardwareTriggerObserved", 5 if order == "FRAME_FIRST" else 15)
    if order == "FRAME_FIRST" and result != "MISSING":
        event("FrameArrived", 10, frameId=frame)
        event("PlcCaptureRequestObserved", 20, plcDmAddress=f"D{address}", plcValue=1)
    else:
        event("PlcCaptureRequestObserved", 10, plcDmAddress=f"D{address}", plcValue=1)
        if result != "MISSING":
            event("FrameArrived", 20, frameId=frame)
    if result == "MISSING":
        event("CaptureTimedOut", 30)
        event("CamDoneAcknowledged", 40, plcDmAddress=f"D{address}", plcValue=3, finsSid=ack["sid"])
    else:
        event("FrameReady", 30, frameId=frame)
        event("CamDoneAcknowledged", 40, plcDmAddress=f"D{address}", plcValue=2, finsSid=ack["sid"])
        if line:
            for index, name in enumerate(channels):
                event("InspectionCompleted", 50 + index, frameId=str(uuid.uuid4()),
                      channelId=name, status=result, details=f"result=unit; parentFrame={frame}")
        else:
            event("InspectionCompleted", 50, frameId=frame, channelId=channels[0], status=result)
    return wire, events, [spec]


class FinsCausalAuditTests(unittest.TestCase):
    def check_failure(self, wire, events, cases, code):
        with self.assertRaises(AuditFailure) as raised:
            verify_evidence(wire, events, cases)
        self.assertEqual(raised.exception.code, code)

    def test_frame_first_real_packet_fields_and_st05_ack(self):
        wire, events, cases = fixture("FRAME_FIRST", "OK")
        self.assertEqual(verify_evidence(wire, events, cases)["physical_capture_ack"], 1)

    def test_request_first_with_ng_still_requires_camdone_two(self):
        wire, events, cases = fixture("REQUEST_FIRST", "NG", "NORMAL_NG")
        self.assertEqual(verify_evidence(wire, events, cases)["end_sequences"], 1)

    def test_all_three_final_branches_check_actual_order_and_readback(self):
        for scenario in ("NORMAL_GOOD", "SCAN_FAILED", "BARCODE_MISMATCH"):
            with self.subTest(scenario=scenario):
                wire, events, cases = fixture(scenario=scenario)
                self.assertEqual(verify_evidence(wire, events, cases)["end_sequences"], 1)

    def test_one_line_scan_parent_produces_two_distinct_children_one_ack(self):
        wire, events, cases = fixture(line=True)
        report = verify_evidence(wire, events, cases)
        self.assertEqual(report["physical_capture_ack"], 1)
        self.assertEqual(sum(x["stage"] == "InspectionCompleted" for x in events), 2)

    def test_line_scan_missing_second_channel_is_not_silent_success(self):
        wire, events, cases = fixture(line=True)
        events.pop()
        self.check_failure(wire, events, cases, "CHANNEL_FANOUT_MISMATCH")

    def test_line_scan_wrong_parent_frame_is_not_allowed(self):
        wire, events, cases = fixture(line=True)
        child = next(x for x in events if x["stage"] == "InspectionCompleted")
        child["details"] = "parentFrame=" + str(uuid.uuid4())
        self.check_failure(wire, events, cases, "LINE_PARENT_FRAME_MISMATCH")

    def test_same_frame_id_on_another_workpiece_is_rejected(self):
        wire, events, cases = fixture()
        source = next(x for x in events if x["stage"] == "FrameArrived")
        events.append(dict(source, workpieceId=str(uuid.uuid4()),
                           captureId=str(uuid.uuid4())))
        self.check_failure(wire, events, cases, "CROSS_WORKPIECE_FRAME_REUSE")

    def test_repeat_plc_poll_is_normal_not_duplicate_camera_request(self):
        wire, events, cases = fixture(repeat_poll=True)
        self.assertEqual(verify_evidence(wire, events, cases)["physical_capture_ack"], 1)

    def test_first_camdone_readback_stale_value_is_not_good(self):
        wire, events, cases = fixture()
        row = wire[3]
        req, rsp = sample_packet(False, 1011, (), row["sid"], (1,))
        row.update(requestHex=req.hex(), responseHex=rsp.hex())
        self.check_failure(wire, events, cases, "CAPTURE_READBACK_MISMATCH")

    def test_final_write_lost_udp_reply_must_not_be_retried_or_assumed_safe(self):
        wire, events, cases = fixture(scenario="NORMAL_GOOD")
        row = next(x for x in wire if x["isWrite"] and x["address"] == 1016)
        row["responseHex"] = ""
        row["outcome"] = "INJECTED_UDP_TIMEOUT"
        self.check_failure(wire, events, cases, "INCOMPLETE_FINAL_WRITE")

    def test_final_first_readback_stale_is_rejected(self):
        wire, events, cases = fixture(scenario="NORMAL_GOOD")
        write = next(x for x in wire if x["isWrite"] and x["address"] == 1016)
        row = next(x for x in wire if not x["isWrite"] and
                   x["address"] == 1016 and x["sequence"] > write["sequence"])
        req, rsp = sample_packet(False, 1016, (), row["sid"], (0,))
        row.update(requestHex=req.hex(), responseHex=rsp.hex())
        self.check_failure(wire, events, cases, "FINAL_READBACK_MISMATCH")

    def test_missing_image_cannot_appear_as_camdone_two(self):
        wire, events, cases = fixture(result="MISSING")
        self.assertEqual(verify_evidence(wire, events, cases)["physical_capture_ack"], 1)
        wire[2]["requestHex"] = wire[2]["requestHex"][:-4] + "0002"
        self.check_failure(wire, events, cases, "FINS_VALUE_MISMATCH")

    def test_missing_frame_cannot_get_algorithm_result(self):
        wire, events, cases = fixture(result="MISSING")
        events.append(dict(events[-1], stage="InspectionCompleted",
                           frameId=str(uuid.uuid4()), channelId="CH06", status="OK"))
        self.check_failure(wire, events, cases, "FALSE_GOOD_ON_MISSING")

    def test_old_d4003_mirror_is_not_two_fins_result_writes(self):
        wire, events, cases = fixture(scenario="NORMAL_GOOD")
        wire = [x for x in wire if x["address"] not in (1016, 2099)]
        self.check_failure(wire, events, cases, "INCOMPLETE_FINAL_WRITE")

    def test_final_write_reverse_order_rejected_despite_same_last_values(self):
        wire, events, cases = fixture(scenario="NORMAL_GOOD")
        # Swap raw datagrams and their metadata but retain ordered receipt sequence.
        relevant = [i for i, x in enumerate(wire) if x["isWrite"] and x["address"] in (1016, 2099)]
        left, right = relevant
        # Build two legitimately matched FINS request/reply pairs with the
        # original SID at each sequence; reversing the actual write order
        # must fail *business chronology*, not a malformed packet check.
        for i, address, value in ((left, 2099, 2), (right, 1016, 1)):
            packet, reply = sample_packet(True, address, (value,), wire[i]["sid"])
            wire[i].update(address=address, values=[value],
                           requestHex=packet.hex(), responseHex=reply.hex())
        self.check_failure(wire, events, cases, "FINAL_WIRE_ORDER")

    def test_sid_mutation_denied_even_if_metadata_claims_success(self):
        wire, events, cases = fixture()
        row = wire[2]
        reply = bytearray.fromhex(row["responseHex"])
        reply[9] ^= 0x01
        row["responseHex"] = reply.hex()
        self.check_failure(wire, events, cases, "FINS_RESPONSE_MISMATCH")

    def test_same_capture_cannot_cross_workpiece(self):
        wire, events, cases = fixture()
        bad = dict(cases[0], workpieceId=str(uuid.uuid4()))
        self.check_failure(wire, events, [bad], "NOT_VERIFIED")

    def test_late_frame_does_not_satisfy_frame_first_contract(self):
        wire, events, cases = fixture("FRAME_FIRST")
        frame = next(e for e in events if e["stage"] == "FrameArrived")
        frame["monotonicTick"] = 22  # PLC request already observed at tick=20.
        self.check_failure(wire, events, cases, "CAUSAL_ORDER")

    def test_missing_raw_packet_cannot_claim_pass(self):
        wire, events, cases = fixture()
        wire[1]["responseHex"] = ""
        self.check_failure(wire, events, cases, "FALSE_SUCCESS")

    def test_actual_socket_loopback_request_reply_decoded(self):
        # Parser sanity only: this responder is a unit fixture, NOT AOI Host.
        server = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        server.bind(("127.0.0.1", 0))
        server.settimeout(2)
        request, response = sample_packet(False, 1011, (), 21, (1,))
        seen = []
        def serve():
            pkt, peer = server.recvfrom(1024)
            seen.append(pkt)
            server.sendto(response, peer)
        worker = threading.Thread(target=serve, daemon=True)
        worker.start()
        try:
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
                client.settimeout(2)
                client.sendto(request, server.getsockname())
                got, _ = client.recvfrom(1024)
            worker.join(2)
            self.assertEqual(seen, [request])
            self.assertEqual(got, response)
            raw = dict(sequence=1, receivedUtc="2026-10-17T08:00:00+00:00",
                       monotonicTimestamp=1234, token=1, sid=21, isWrite=False,
                       address=1011, values=[], requestHex=seen[0].hex(),
                       responseHex=got.hex(), outcome="OK")
            self.assertEqual(parse_trace([raw])[0].reply_words, (1,))
        finally:
            server.close()


if __name__ == "__main__":
    unittest.main()
