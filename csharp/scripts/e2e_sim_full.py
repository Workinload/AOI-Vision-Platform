#!/usr/bin/env python3
"""Real five OS-process SIM_FULL continuous 11-channel conveyor acceptance.

All sockets are 127.0.0.1. Test-only D4000+ are NOT physical PLC addresses.
Run separately from the single-piece SIM_LIGHT smoke (fixed ports).
"""
import argparse
import hashlib
import json
import os
import re
from pathlib import Path
import socket
import sqlite3
import struct
import zlib
import subprocess
import sys
import tempfile
import time


def fins_read(address):
    request = bytes([128, 0, 2, 0, 1, 0, 0, 99, 0, 42, 1, 1, 130,
                     address >> 8, address & 255, 0, 0, 1])
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.settimeout(1)
        sock.sendto(request, ('127.0.0.1', 19600))
        response, peer = sock.recvfrom(256)
        assert peer[0] == '127.0.0.1' and len(response) == 16
        assert response[0] == 192 and response[9] == 42 and response[12:14] == b'\x00\x00'
        return response[14] * 256 + response[15]


def png_preview(port, workpiece_id, channel):
    """Actual binary PNG frame from the isolated node IPC; never a filesystem shortcut."""
    with socket.create_connection(('127.0.0.1', port), timeout=3) as sock:
        sock.settimeout(3)
        sock.sendall((workpiece_id + '|' + channel + '\n').encode('ascii'))
        def receive(count):
            buffer = bytearray()
            while len(buffer) < count:
                data = sock.recv(count - len(buffer))
                if not data:
                    raise EOFError('truncated preview IPC payload')
                buffer.extend(data)
            return bytes(buffer)
        length = int.from_bytes(receive(4), 'big', signed=True)
        assert 0 <= length <= 2 * 1024 * 1024, 'unsafe frame preview length'
        return receive(length) if length else None


def roi_edit(port, edit, token):
    with socket.create_connection(('127.0.0.1', port), timeout=3) as sock:
        sock.settimeout(3)
        request = json.dumps({'edit': edit, 'accessToken': token},
                             separators=(',', ':')) + '\n'
        sock.sendall(request.encode('utf-8'))
        with sock.makefile('r', encoding='utf-8') as reader:
            answer = reader.readline()
            assert answer, 'Missing durable ROI edit response'
            return json.loads(answer)


def core_snapshot(port):
    with socket.create_connection(('127.0.0.1', port), timeout=3) as sock:
        sock.settimeout(3)
        with sock.makefile('r', encoding='utf-8') as reader:
            return json.loads(reader.readline())


def core_report():
    with socket.create_connection(('127.0.0.1', 19651), timeout=4) as sock:
        sock.settimeout(4)
        sock.sendall(b'REPORT\n')
        with sock.makefile('r', encoding='utf-8') as reader:
            result = reader.readline()
            assert result, 'No Master read-only report response'
            return json.loads(result)


def query(path, statement):
    if not path.is_file():
        return None
    try:
        with sqlite3.connect(path.resolve().as_uri() + '?mode=ro', uri=True, timeout=1) as db:
            return db.execute(statement).fetchall()
    except sqlite3.OperationalError as exc:
        if 'no such table' in str(exc) or 'unable to open database' in str(exc):
            return None
        raise


def scalar(path, statement):
    value = query(path, statement)
    return None if value is None or len(value) == 0 else value[0][0]


def make_png(path, bright_defect=False, line_parent=False):
    # Standard-library-only 8-bit grayscale PNG fixture: actual disk PNG, not
    # a mocked OpenCV result. CH06 has one clear foreground contour.
    width = 64
    height = 32 if line_parent else 64
    raw = bytearray()
    for y in range(height):
        raw.append(0)
        for x in range(width):
            raw.append(255 if bright_defect and 18 <= x < 42 and 17 <= y < 39 else 0)
    def chunk(tag, data):
        return struct.pack('>I', len(data)) + tag + data + struct.pack(
            '>I', zlib.crc32(tag + data) & 0xffffffff)
    png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR',
        struct.pack('>IIBBBBB', width, height, 8, 0, 0, 0, 0)) + \
        chunk(b'IDAT', zlib.compress(bytes(raw))) + chunk(b'IEND', b'')
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(png)


def verify(root, count, flip, real_images=False, legacy_shadow=False,
           line_row_mapping='DEMO_EVEN_ODD'):
    master = root / 'master/production.db'
    slave = root / 'slave/production.db'
    ledger = root / 'master/sim-full-cycle.db'
    expect_exit = (flip - 1) if flip else count
    expected_counts = {
        (master, 'workpieces'): count, (slave, 'workpieces'): count,
        (master, 'channel_results'): 11 * count,
        (slave, 'channel_results'): 6 * count
    }
    for (path, table), expected in expected_counts.items():
        assert scalar(path, 'SELECT COUNT(*) FROM ' + table) == expected, (path, table)
        assert scalar(path, 'PRAGMA integrity_check') == 'ok', path

    identities = query(master, 'SELECT workpiece_id,identity_json,final_json FROM workpieces')
    peer_identities = query(slave, 'SELECT workpiece_id,identity_json FROM workpieces')
    assert len(identities) == len(peer_identities) == count
    assert {x[0]: x[1] for x in identities} == {x[0]: x[1] for x in peer_identities}
    assert len({x[0] for x in identities}) == count, 'WorkpieceId reuse'
    for _, identity, final in identities:
        wp = json.loads(identity)
        assert wp['workpieceId'] and wp['epoch'] == 1
        if final:
            decision = json.loads(final)
            assert decision['isSimulation'] is True and decision['disposition'] == (
                'reject' if real_images else 'good')
            assert len(decision['expected']) == len(decision['received']) == 10
            assert 'SIM_ONLY' in decision['proof']
    assert sum(x[2] is not None for x in identities) == expect_exit

    rows = query(ledger, 'SELECT token,identity_json,phase,disposition FROM sim_full_tokens ORDER BY token')
    assert len(rows) == count and [r[0] for r in rows] == list(range(1, count + 1)), rows
    id_set = {x[0] for x in identities}
    assert {json.loads(x[1])['workpieceId'] for x in rows} == id_set
    assert sum(x[2] == 'exited' for x in rows) == expect_exit
    for token, identity, phase, outcome in rows:
        decision = next(x[2] for x in identities if x[0] == json.loads(identity)['workpieceId'])
        assert (phase == 'exited') == (decision is not None), token
        assert outcome == (('Reject' if real_images else 'Good') if phase == 'exited' else None), token
    if flip:
        assert rows[flip - 1][2] == 'registered', 'flipped exit token was released'
        assert all(x[2] != 'exited' for x in rows[flip - 1:]), 'later pieces overtook HOLD'
        holds = query(master, "SELECT workpiece_id,reason_code,detail FROM workpiece_holds")
        expected_held_id = json.loads(rows[flip - 1][1])['workpieceId']
        assert holds and len(holds) == 1 and holds[0][0] == expected_held_id, holds
        assert holds[0][1] == 'END_BARCODE_MISMATCH' and 'manual reconciliation' in holds[0][2]
        assert scalar(master, "SELECT COUNT(*) FROM flight_events WHERE kind='WORKPIECE_HOLD'") == 1
    else:
        assert scalar(master, "SELECT COUNT(*) FROM workpiece_holds") == 0

    for node, c, asset_count in [('master', 5, 6), ('slave', 6, 7)]:
        path = root / node / 'vision/vision-assets.sqlite'
        assert scalar(path, 'SELECT COUNT(*) FROM vision_results') == count * c, node
        assert scalar(path, 'SELECT COUNT(*) FROM vision_assets') == count * asset_count, node
        assert scalar(path, "SELECT COUNT(*) FROM vision_assets WHERE kind='parent'") == count, node
        assert scalar(path, "SELECT COUNT(*) FROM vision_assets WHERE kind='child'") == count * 2, node

    if legacy_shadow and line_row_mapping == 'LEGACY_DROP_ROW0':
        for node, first_id, second_id in [('master', 'CH10', 'CH11'),
                                           ('slave', 'CH08', 'CH09')]:
            db = root / node / 'vision/vision-assets.sqlite'
            children = [json.loads(row[0]) for row in
                        query(db, "SELECT asset_json FROM vision_assets WHERE kind='child'")]
            assert len(children) == count * 2, node
            for channel, height, label in [
                (first_id, 16, 'legacy-interleaved-row1-step2'),
                (second_id, 15, 'legacy-interleaved-row2-step2')
            ]:
                matched = [asset for asset in children if asset['channelId'] == channel]
                assert len(matched) == count, (node, channel)
                assert all(asset['height'] == height and
                           asset['pixelTransform'] == label and
                           asset['parentFrameId'] for asset in matched), (node, channel)
        print('SIM_LEGACY_LINE_ROW_MAPPING_PASS row0_discarded=1 ' +
              'parent_height=32 first_child=16 second_child=15 dual_line_parents=1', flush=True)

    for sender, expected in [('master', count * (6 if legacy_shadow else 1)),
                             ('slave', count * 6)]:
        path = root / sender / 'production.db'
        assert scalar(path, 'SELECT COUNT(*) FROM outbox WHERE acknowledged_utc IS NOT NULL') == expected, sender
        assert scalar(path, 'SELECT COUNT(*) FROM outbox') == expected, sender
    assert scalar(master, 'SELECT COUNT(*) FROM inbox') == count * 6
    assert scalar(slave, 'SELECT COUNT(*) FROM inbox') == count * (6 if legacy_shadow else 1)
    if legacy_shadow:
        assert scalar(slave, 'SELECT COUNT(*) FROM capture_triggers') == count * 5
        assert scalar(slave, 'SELECT COUNT(*) FROM capture_triggers WHERE processed_utc IS NOT NULL') == count * 5

    assert fins_read(4005) == expect_exit, 'virtual hardware released unexpected number'
    assert fins_read(4002) == (flip if flip else 0), 'FIFO exit token is wrong'
    assert fins_read(4006) == (count - expect_exit if flip else 0), 'sim conveyor active count wrong'
    if flip:
        assert scalar(master, 'SELECT COUNT(*) FROM workpieces WHERE final_json IS NULL') == count - expect_exit
    else:
        assert fins_read(4000) == 0
        assert scalar(ledger, "SELECT COUNT(*) FROM sim_full_tokens WHERE phase='registered'") == 0
    return True


def verify_missing_slave_frame(root, missed_token=3, device='HIK-S-2'):
    """Check true five-process fail-closed behavior without pretending all images exist."""
    assert missed_token == 3 and device == 'HIK-S-2'
    master = root / 'master/production.db'
    slave = root / 'slave/production.db'
    for path in (master, slave):
        assert scalar(path, 'PRAGMA integrity_check') == 'ok', path
    items = query(root / 'master/sim-full-cycle.db',
                  'SELECT token,identity_json,phase FROM sim_full_tokens ORDER BY token')
    assert len(items) == 3 and [x[0] for x in items] == [1, 2, 3], items
    faulty = json.loads(items[2][1])['workpieceId']
    assert items[2][2] == 'registered', 'Missing physical frame must not be ejected'
    assert scalar(master, "SELECT COUNT(*) FROM workpieces WHERE final_json IS NOT NULL") == 2
    assert scalar(master, "SELECT COUNT(*) FROM workpieces WHERE final_json LIKE '%good%'") == 0
    assert scalar(master, "SELECT COUNT(*) FROM workpieces WHERE final_json LIKE '%reject%'") == 2
    for node in (master, slave):
        holds = query(node, "SELECT workpiece_id,reason_code,detail FROM workpiece_holds")
        assert len(holds) == 1 and holds[0][0] == faulty and holds[0][1] == 'PHYSICAL_FRAME_MISSING', holds
        assert device in holds[0][2]
    assert scalar(master, "SELECT COUNT(*) FROM flight_events WHERE kind='REMOTE_CAPTURE_HOLD'") == 1
    assert scalar(slave, "SELECT COUNT(*) FROM flight_events WHERE kind='CAPTURE_FAULT_OUTBOX_QUEUED'") == 1
    assert scalar(slave, """SELECT COUNT(*) FROM outbox
            WHERE envelope_json LIKE '%capture_fault%' AND acknowledged_utc IS NOT NULL""") == 1
    assert scalar(master, """SELECT COUNT(*) FROM inbox
            WHERE sender_node_id='slave'""") >= 13, 'Slave fault was not persisted before ACK'
    faulty_channels = query(master, """SELECT result_json FROM channel_results
                                      WHERE workpiece_id='""" + faulty + "'")
    assert not any(json.loads(row[0])['channelId'] == 'CH02' for row in faulty_channels), (
        'Missing camera CH02 must not fabricate a result')
    wire_path = root / 'p0-fins-wire.jsonl'
    assert wire_path.is_file()
    packets = [json.loads(line) for line in wire_path.read_text(encoding='utf-8').splitlines()
               if line.strip()]
    acks = [p for p in packets if p['Token'] == missed_token and
            p['Address'] == 1008 and p['IsWrite'] and p['Values'] == [3] and
            p['Outcome'] == 'OK' and p['ResponseHex'].startswith('C0')]
    assert len(acks) == 1, 'Missing Slave image must write one real D1008=3 FINS'
    assert not any(p['Token'] == missed_token and p['IsWrite'] and
                   p['Address'] in (1016, 2099) for p in packets), (
        'Faulty workpiece must not receive any normal final PLC write')
    assert fins_read(4005) == 2, 'Only healthy predecessors may leave the virtual PLC'
    assert fins_read(4002) == 3, 'Fault token must remain physically held'
    master_log = (root / 'master.log').read_text(encoding='utf-8')
    slave_log = (root / 'slave.log').read_text(encoding='utf-8')
    assert 'SIM_MASTER_REMOTE_CAPTURE_HOLD token=3' in master_log
    assert 'SIM_FULL_HOLD token=3' in master_log
    assert 'SIM_SLAVE_FAULT_QUEUED token=3' in slave_log
    assert 'SIM_SLAVE_TRIGGER_COMPLETED' in slave_log
    print('A0_SLAVE_MISSING_FRAME_E2E_PASS token=3 device=HIK-S-2 ' +
          'actual_fins_D1008_3=1 slave_wal_hold=1 master_wal_hold=1 ' +
          'peer_ack=1 physical_exit_blocked=1 unaffected_predecessors=2 ' +
          'master_core_alive=1 slave_core_alive=1 false_good=0', flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--evidence-root')
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--count', type=int, default=5)
    parser.add_argument('--flip-token', type=int, default=0)
    parser.add_argument('--repeat-barcode', action='store_true')
    parser.add_argument('--with-images', action='store_true',
                        help='Replay actual 8-bit disk PNG inputs; CH06 deliberately NG')
    parser.add_argument('--with-product', action='store_true',
                        help='Bootstrap/tune/publish/dual-activate an A6 immutable SIM_ONLY recipe before running')
    parser.add_argument('--legacy-shadow', action='store_true',
                        help='Exercise real FINS D1999/D1011/D1016/D2099 in station-triggered PLC shadow')
    parser.add_argument('--st05-order', choices=['REQUEST_FIRST', 'FRAME_FIRST'],
                        default='REQUEST_FIRST')
    parser.add_argument('--line-row-mapping',
                        choices=['DEMO_EVEN_ODD', 'LEGACY_DROP_ROW0'],
                        default='DEMO_EVEN_ODD')
    parser.add_argument('--missing-frame-token', type=int, default=0,
                        help='SIM_ONLY inject one missing physical camera frame')
    parser.add_argument('--missing-frame-device', default='HIK-S-2',
                        help='Exact physical camera for an injected hard-frame absence')
    parser.add_argument('--timeout', type=float, default=70.0)
    args = parser.parse_args()
    if args.count < 3 or args.count > 60 or args.flip_token not in (0, 3) or args.count < args.flip_token:
        parser.error('CI supports 3..60 virtual items and optional deterministic fault token=3')
    if args.legacy_shadow and not args.with_images:
        parser.error('P0 physical shadow requires --with-images so there is no synthetic frame fallback')
    if args.line_row_mapping != 'DEMO_EVEN_ODD' and not args.legacy_shadow:
        parser.error('Legacy row layout requires opt-in external PLC shadow mode')
    if args.missing_frame_token and not (args.legacy_shadow and args.with_images and
                                        args.count == 3 and args.missing_frame_token == 3 and
                                        args.flip_token == 0 and
                                        args.missing_frame_device == 'HIK-S-2'):
        parser.error('First P0 fault run must be 3 PNG items, token3/HIK-S-2, legacy shadow')
    source = Path(__file__).resolve().parent.parent
    evidence = Path(args.evidence_root).resolve() if args.evidence_root else Path(tempfile.mkdtemp(prefix='aoi-simfull-'))
    if evidence.exists() and any(evidence.iterdir()):
        raise RuntimeError('SIM_FULL cannot reuse a nonempty evidence directory')
    evidence.mkdir(parents=True, exist_ok=True)
    host = source / 'src/AOI.Host/bin/Debug/net10.0/AOI.Host.dll'
    ui = source / 'src/AOI.Ui.Avalonia/bin/Debug/net10.0/AOI.Ui.Avalonia.dll'
    assert host.is_file() and ui.is_file(), 'Build AOI.slnx first'
    env = dict(os.environ)
    if args.with_images:
        image_root = evidence / 'operator-images'
        for channel in range(1, 8):
            make_png(image_root / f'CH{channel:02d}.png', bright_defect=channel == 6)
        make_png(image_root / 'LINE-S-8.png', line_parent=True)
        make_png(image_root / 'LINE-M-2.png', line_parent=True)
        env['AOI_SIM_IMAGE_ROOT'] = str(image_root)
        env['AOI_SIM_IMAGE_LAYOUT'] = 'FLAT'
    else:
        env.pop('AOI_SIM_IMAGE_ROOT', None)
        env.pop('AOI_SIM_IMAGE_LAYOUT', None)
    env.update({'AOI_SIM_MODE': 'SIM_FULL', 'AOI_SIM_CHANNEL_SET': 'ALL_11',
                'AOI_SIM_ITEM_COUNT': str(args.count), 'AOI_SIM_MAX_IN_FLIGHT': '3',
                'AOI_SIM_END_BARCODE_FLIP_TOKEN': str(args.flip_token),
                'AOI_SIM_REPEAT_BARCODE': '1' if args.repeat_barcode else '0',
                'AOI_SIM_PLC_PROFILE': 'LEGACY_PLC_SHADOW' if args.legacy_shadow else 'FIFO_DEMO',
                'AOI_SIM_ST05_ORDER': args.st05_order,
                'AOI_SIM_LINE_ROW_MAPPING': args.line_row_mapping,
                'AOI_SIM_MISSING_FRAME_TOKEN': str(args.missing_frame_token) if args.missing_frame_token else '',
                'AOI_SIM_MISSING_FRAME_DEVICE': args.missing_frame_device,
                'DOTNET_CLI_TELEMETRY_OPTOUT': '1',
                'AOI_SIM_CONFIG_TOKEN': 'SIM_ONLY_E2E_ROI_OPERATOR_TOKEN_2026'})
    if args.with_product:
        cli = source / 'src/AOI.Solutions.Cli/bin/Debug/net10.0/AOI.Solutions.Cli.dll'
        assert cli.is_file(), 'Build AOI.slnx including A6 Solutions.Cli first'
        def recipe(*parts):
            p = subprocess.run([args.dotnet, str(cli), *map(str, parts)],
                               env=env, cwd=source, capture_output=True, text=True, timeout=15)
            assert p.returncode == 0, (parts, p.stdout, p.stderr)
            print(p.stdout.strip(), flush=True)
            return p.stdout
        recipe('bootstrap', evidence, 'SIM_PRODUCT_A', 'Test SIM product A', 'sim_engineer')
        recipe('tune', evidence, 'SIM_PRODUCT_A', 'ST05', 1, 110, 0.1, 'sim_engineer')
        published = recipe('publish', evidence, 'SIM_PRODUCT_A', 'sim_engineer')
        product_release = published.split('release=')[1].split()[0]
        assert product_release.startswith('SIMREL-')
        recipe('activate', evidence, product_release, 'sim_engineer')
        assert 'real_writes=0' in recipe('status', evidence)
    else:
        product_release = 'SIM_LIGHT_TEST_RELEASE'
    processes, logs = [], []
    def start(name, dll, *flags):
        stream = (evidence / (name + '.log')).open('w', encoding='utf-8')
        logs.append(stream)
        proc = subprocess.Popen([args.dotnet, str(dll), *flags], env=env, cwd=source,
                                stdout=stream, stderr=subprocess.STDOUT)
        processes.append((name, proc))
        print(f'SIM_FULL_PROCESS_STARTED name={name} pid={proc.pid}', flush=True)

    try:
        start('vplc', host, 'vplc', '--data', str(evidence))
        start('slave', host, 'slave', '--data', str(evidence))
        start('master', host, 'master', '--data', str(evidence))
        start('master-ui', ui, '--master', '--headless', '--once')
        start('slave-ui', ui, '--slave', '--headless', '--once')
        print('SIM_FULL_FIVE_PROCESSES_LAUNCHED', flush=True)
        target = (args.missing_frame_token - 1 if args.missing_frame_token else
                  args.flip_token - 1 if args.flip_token else args.count)
        deadline = time.monotonic() + args.timeout
        while time.monotonic() < deadline:
            for name, proc in processes[:3]:
                if proc.poll() is not None:
                    raise RuntimeError(f'{name} terminated exit={proc.returncode}')
            n = scalar(evidence / 'master/production.db',
                       'SELECT COUNT(*) FROM workpieces')
            m = scalar(evidence / 'master/production.db',
                       'SELECT COUNT(*) FROM workpieces WHERE final_json IS NOT NULL')
            s = scalar(evidence / 'slave/production.db',
                       'SELECT COUNT(*) FROM channel_results')
            acknowledgements = scalar(evidence / 'slave/production.db',
                       'SELECT COUNT(*) FROM outbox WHERE acknowledged_utc IS NOT NULL')
            if args.missing_frame_token:
                if (n == args.count and m == target and
                    scalar(evidence / 'master/production.db',
                           'SELECT COUNT(*) FROM workpiece_holds') == 1 and
                    scalar(evidence / 'slave/production.db',
                           'SELECT COUNT(*) FROM workpiece_holds') == 1 and
                    acknowledgements >= 13 and fins_read(4005) == target):
                    break
            elif n == args.count and m == target and s == args.count * 6 and acknowledgements == args.count * 6:
                if fins_read(4005) == target:
                    break
            time.sleep(0.15)
        else:
            raise TimeoutError('SIM_FULL timed out without complete concurrent production evidence')
        # A matched set must remain stable, especially when the front item is held.
        time.sleep(0.55)
        if args.missing_frame_token:
            verify_missing_slave_frame(evidence, args.missing_frame_token,
                                       args.missing_frame_device)
            for name, proc in processes[:3]:
                assert proc.poll() is None, name + ' Core quit during missing frame'
            return 0
        verify(evidence, args.count, args.flip_token, args.with_images,
               args.legacy_shadow, args.line_row_mapping)
        if args.legacy_shadow:
            wire_path = evidence / 'p0-fins-wire.jsonl'
            assert wire_path.is_file(), 'Shadow did not export real UDP request/response evidence'
            packets = [json.loads(line) for line in wire_path.read_text(encoding='utf-8').splitlines()
                       if line.strip()]
            def written(token, address, value):
                return [p for p in packets if p['Token'] == token and p['Address'] == address
                        and p['IsWrite'] and p['Values'] == [value] and p['Outcome'] == 'OK'
                        and p['ResponseHex'].startswith('C0')]
            for token in range(1, target + 1):
                assert len(written(token, 1999, 2)) == 1, ('missing D1999 actual FINS ACK', token)
                # Exactly NINE physical requests, not 11 invented camera exposures.
                for camera in range(1006, 1015):
                    offers = [p for p in packets if p['Token'] == token and
                              p['Address'] == 4300 and p['IsWrite'] and
                              p['Values'] == [token, camera] and p['Outcome'] == 'OK']
                    assert len(offers) == 1, ('missing physical camera PLC offer', token, camera)
                    cam_ack = written(token, camera, 2)
                    assert len(cam_ack) == 1, ('missing actual FINS CamDone2', token, camera)
                    assert offers[0]['Sequence'] < cam_ack[0]['Sequence']
                    assert any(p['Token'] == token and p['Address'] == camera and
                               not p['IsWrite'] and p['Sequence'] > cam_ack[0]['Sequence']
                               and p['ResponseHex'].startswith('C0') for p in packets), (
                               'physical FINS CamDone readback missing', token, camera)
                desired = 2 if args.with_images else 1
                first = written(token, 1016, desired)
                second = written(token, 2099, 2)
                assert len(first) == len(second) == 1, ('missing actual dual final FINS write', token)
                assert first[0]['Sequence'] < second[0]['Sequence'], (
                    'PLC final FINS datagrams were sent in the wrong order', token)
                # Both old-address FINS writes require independent observed readback.
                assert any(p['Token'] == token and p['Address'] == 1016 and not p['IsWrite']
                           and p['Sequence'] > first[0]['Sequence'] and
                           p['Sequence'] < second[0]['Sequence'] and
                           p['ResponseHex'].startswith('C0') for p in packets)
                assert any(p['Token'] == token and p['Address'] == 2099 and not p['IsWrite']
                           and p['Sequence'] > second[0]['Sequence'] and
                           p['ResponseHex'].startswith('C0') for p in packets)
            if args.flip_token:
                # A different end barcode is a real old-PLC reverse-order
                # exchange, not the old D4003 fake mirror or an unsafe exit.
                token = args.flip_token
                first = written(token, 2099, 4)
                second = written(token, 1016, 2)
                assert len(first) == len(second) == 1, (
                    'missing actual FINS abnormal D2099=4 then D1016=2', token)
                assert first[0]['Sequence'] < second[0]['Sequence']
                assert any(p['Token'] == token and p['Address'] == 2099 and
                           not p['IsWrite'] and first[0]['Sequence'] < p['Sequence'] <
                           second[0]['Sequence'] for p in packets), (
                    'no first abnormal write readback', token)
                assert any(p['Token'] == token and p['Address'] == 1016 and
                           not p['IsWrite'] and p['Sequence'] > second[0]['Sequence']
                           for p in packets), ('no final reject write readback', token)
                assert not written(token, 2099, 2), (
                    'mismatched barcode was falsely marked as normal scan', token)
                assert not any(p['Token'] > token and p['IsWrite'] and
                               p['Address'] in (1016, 2099) for p in packets), (
                    'later physically blocked pieces illegally received finals')
                print('A0_LEGACY_BARCODE_MISMATCH_FINS_PASS token=' + str(token) +
                      ' D2099_4_then_D1016_2=1 actual_udp=1 no_fifo_ejection=1 '
                      'no_automatic_retry=1', flush=True)
            for node, physical_count in [('master', 4), ('slave', 5)]:
                db = evidence / node / 'timing-events.sqlite'
                for stage in ('FrameReady', 'PlcCaptureRequestObserved'):
                    assert scalar(db, "SELECT COUNT(*) FROM timing_events WHERE stage='" +
                                  stage + "'") == physical_count * args.count, (node, stage)
            print('A0_LEGACY_SHADOW_FINS_NORMAL_PASS items=' + str(target) +
                  ' physical_events_per_item=9 logical_results=11 D1999=1 ' +
                  'D1016_then_D2099=1 actual_udp=1 ordering=' + args.st05_order +
                  ' dual_core=1 real_writes=0', flush=True)
        master_log = (evidence / 'master.log').read_text(encoding='utf-8')
        virtual_log = (evidence / 'vplc.log').read_text(encoding='utf-8')
        # Different OS scheduling can finish item #1 before slot #3 fills.
        # Demand evidence of genuine overlap, with a hard upper bound of three.
        observed = [int(v) for v in re.findall(
            r'SIM_FULL_VPLC_LOAD token=\d+ active=(\d+)', virtual_log)]
        assert observed and 2 <= max(observed) <= 3, (
            'Conveyor must show real overlap without exceeding three slots', observed)
        assert master_log.count('SIM_FULL_LOADED') == args.count
        assert master_log.count('SIM_FULL_EXITED') == target
        assert master_log.count('SIM_BRIDGE_LOAD') == args.count, 'A2 mechanical load missing'
        assert master_log.count('SIM_BRIDGE_EJECTED') == target, 'A2 token ejection mismatch'
        assert master_log.count('SIM_BRIDGE_STATION_COMPLETE') >= target * 7, (
            'A1 all seven stations must complete before a final can be dispatched')
        assert master_log.count('SIM_BRIDGE_MECHANICAL event=SensorTriggered') >= target * 8
        flow_db = evidence / 'master/production-progress.db'
        assert scalar(flow_db, "SELECT COUNT(*) FROM a1_production_flow_events "
                      "WHERE json_extract(event_json,'$.kind')='stationCompleted'") >= target * 7
        assert scalar(flow_db, "SELECT COUNT(*) FROM a1_production_flow_events "
                      "WHERE json_extract(event_json,'$.kind')='ejectionConfirmed'") == target
        if args.flip_token:
            assert f'SIM_FULL_HOLD token={args.flip_token}' in master_log
            if args.legacy_shadow:
                assert f'SIM_LEGACY_ABNORMAL_FINS_PASS token={args.flip_token} ' in master_log
                assert 'scenario=BarcodeMismatch' in master_log
                assert 'physical_exit=HOLD' in master_log
        for name, proc in processes[3:]:
            assert proc.wait(timeout=10) == 0, name + ' UI failed'
            ui_log = (evidence / (name + '.log')).read_text(encoding='utf-8')
            assert 'UI_HEADLESS_SNAPSHOT_OK' in ui_log, name
            assert 'UI_PLC_ENGINEERING_OK' in ui_log and 'signals=45 gates=14 live=False' in ui_log, name
            if args.with_product:
                assert 'UI_SIM_PRODUCT_OK' in ui_log and 'stations=7' in ui_log, name
        with socket.create_connection(('127.0.0.1', 19621), timeout=3) as sock:
            with sock.makefile('r', encoding='utf-8') as view:
                snapshot = json.loads(view.readline())
        assert snapshot['simulationMode'] == 'SIM_FULL', snapshot['simulationMode']
        assert snapshot['realHardwareWritesEnabled'] is False
        motion = snapshot.get('conveyor')
        assert motion and motion['loadedCount'] == args.count, motion
        assert motion['exitedCount'] == target, motion
        assert len(motion['active']) == args.count - target, motion
        assert motion['tick'] > 10 and not motion['stuckSensors'], motion
        assert len({row['workpieceId'] for row in motion['active']}) == len(motion['active'])
        if args.flip_token:
            assert any(row['stationId'] == 'ST11' and row['readyForExit']
                       for row in motion['active']), motion
        print(f"SIM_MECHANICAL_A1_A2_IPC_PASS loaded={motion['loadedCount']} "
              f"out={motion['exitedCount']} in_flight={len(motion['active'])} "
              "seven_station_progress=1 no_early_eject=1", flush=True)
        hold_overview = snapshot.get('holds')
        assert hold_overview and hold_overview['activeCount'] == (1 if args.flip_token else 0), hold_overview
        assert len(hold_overview['recent']) == (1 if args.flip_token else 0), hold_overview
        if args.flip_token:
            held = hold_overview['recent'][0]
            assert held['reasonCode'] == 'END_BARCODE_MISMATCH'
            assert held['workpieceId'] == json.loads(
                query(evidence / 'master/sim-full-cycle.db',
                      'SELECT identity_json FROM sim_full_tokens WHERE token=3')[0][0]
            )['workpieceId']
        print(f"SIM_FULL_HOLD_IPC_PASS active={hold_overview['activeCount']} fail_closed=1", flush=True)
        response = core_report()
        assert response['accepted'] is True and response['reports']['isSimulation'] is True, response
        report = response['reports']
        assert report['allTimeMatches'] == args.count and len(report['recent']) == args.count, report
        assert len({r['workpieceId'] for r in report['recent']}) == args.count
        expected_good = (0 if args.with_images else target)
        expected_ng = (target if args.with_images else 0)
        expected_hold = (1 if args.flip_token else 0)
        expected_pending = args.count - expected_good - expected_ng - expected_hold
        for period in (report['today'], report['thisWeek']):
            assert period['total'] == args.count, period
            assert period['good'] == expected_good and period['ng'] == expected_ng, period
            assert period['hold'] == expected_hold and period['inProgress'] == expected_pending, period
            assert period['goodRate'] == ((expected_good / target) if target else None), period
        if args.repeat_barcode:
            assert len({r['barcode'] for r in report['recent']}) == 1
        if args.flip_token:
            assert any(r['disposition'] == 'Hold' and r['holdReason'] == 'END_BARCODE_MISMATCH'
                       for r in report['recent']), report
        print(f"SIM_REPORT_A7_IPC_PASS total={args.count} good={expected_good} "
              f"ng={expected_ng} hold={expected_hold} unfinished={expected_pending} "
              "read_only=1 days=2", flush=True)
        plc_registry = snapshot.get('plcEngineering')
        assert plc_registry and plc_registry['isFieldLive'] is False, plc_registry
        assert len(plc_registry['signals']) == 45 and len(plc_registry['gates']) == 14
        assert sum(g['status'] == 'BLOCKED' for g in plc_registry['gates']) == 7
        assert sum(g['status'] == 'OPEN' for g in plc_registry['gates']) == 7
        assert not any(x['productionWriteEnabled'] for x in plc_registry['signals'])
        assert not any(x['productionWriteAllowed'] for x in plc_registry['gates'])
        assert next(x for x in plc_registry['signals'] if x['signalId'] == 'SIG_FINAL_QUALITY')['dmAddress'] == 'D1016'
        print('SIM_FULL_PLC_ENGINEERING_IPC_PASS signals=45 fg=14 field_live=0 real_writes=0', flush=True)
        overview = snapshot.get('production')
        assert overview and overview['total'] == args.count, overview
        assert overview['good'] == (0 if args.with_images else target), overview
        assert overview['reject'] == (target if args.with_images else 0), overview
        assert overview['unfinished'] == args.count - target, overview
        assert len(overview['recent']) == args.count, overview
        assert len({row['workpieceId'] for row in overview['recent']}) == args.count
        assert all(row['capturedChannels'] == 11 for row in overview['recent']), overview
        assert sum(row['final'] == ('reject' if args.with_images else 'good')
                   for row in overview['recent']) == target
        assert snapshot['latestWorkpiece'] is not None
        assert snapshot['latestWorkpiece']['releaseId'] == product_release
        assert snapshot['latestWorkpiece']['epoch'] == 1
        if args.with_product:
            active = snapshot.get('activeProduct')
            assert active and active['isSimulation'] is True, active
            assert active['releaseId'] == product_release and active['solutionId'] == 'SIM_PRODUCT_A', active
            assert active['epoch'] == 1 and len(active['stations']) == 7, active
            assert next(s for s in active['stations'] if s['stationId'] == 'ST05')['brightThreshold'] == 110
            assert next(s for s in active['stations'] if s['stationId'] == 'ST05')['minimumBrightFraction'] == 0.1
            assert all(json.loads(row[1])['releaseId'] == product_release
                       for row in query(evidence / 'master/production.db',
                                        'SELECT workpiece_id, identity_json FROM workpieces'))
            print(f'SIM_PRODUCT_RUNTIME_PASS release={product_release} stations=7 '
                  'master=5 slave=6 epoch=1 virtual_pixels=1', flush=True)
        else:
            assert snapshot.get('activeProduct') is None, snapshot.get('activeProduct')
        # A4 operator preview is real pixel data delivered by the owning Core,
        # NOT a UI-side path to a local SQLite file.
        first = query(evidence / 'master/production.db',
                      'SELECT workpiece_id FROM workpieces ORDER BY created_utc LIMIT 1')[0][0]
        master_frame = json.loads(query(evidence / 'master/production.db',
            "SELECT result_json FROM channel_results WHERE workpiece_id='" + first +
            "' AND channel_id='CH06'")[0][0])
        slave_frame = json.loads(query(evidence / 'slave/production.db',
            "SELECT result_json FROM channel_results WHERE workpiece_id='" + first +
            "' AND channel_id='CH02'")[0][0])
        local_png = png_preview(19631, first, 'CH06')
        slave_png = png_preview(19632, first, 'CH02')
        assert local_png and slave_png and local_png.startswith(b'\x89PNG')
        assert hashlib.sha256(local_png).hexdigest() == master_frame['sourceFrameSha256']
        assert hashlib.sha256(slave_png).hexdigest() == slave_frame['sourceFrameSha256']
        assert png_preview(19631, first, 'CH02') is None, 'Master must not serve Slave-owned source frame'
        assert png_preview(19632, first, 'CH06') is None, 'Slave must not serve Master-owned source frame'
        print('SIM_FULL_PREVIEW_IPC_PASS local_2=1 sha256=2 remote_leak=0', flush=True)
        # Real writable-but-DRAFT-ONLY operator IPC, launched inside BOTH Cores;
        # CAS revisions, token auth and node ownership are mandatory.
        token = env['AOI_SIM_CONFIG_TOKEN']
        master_roi = {'channelId': 'CH06', 'x': 0.15, 'y': 0.1,
                      'width': 0.7, 'height': 0.8, 'expectedRevision': 0,
                      'actor': 'sim_operator'}
        slave_roi = {'channelId': 'CH02', 'x': 0.1, 'y': 0.2,
                     'width': 0.8, 'height': 0.6, 'expectedRevision': 0,
                     'actor': 'sim_operator'}
        ack_master = roi_edit(19641, master_roi, token)
        ack_slave = roi_edit(19642, slave_roi, token)
        assert ack_master['accepted'] and ack_master['saved']['revision'] == 1, ack_master
        assert ack_slave['accepted'] and ack_slave['saved']['revision'] == 1, ack_slave
        assert roi_edit(19641, master_roi, token)['accepted'] is False, 'stale revision accepted'
        assert roi_edit(19641, master_roi, 'incorrect_ROI_2026_access_token')['accepted'] is False, 'bad token accepted'
        assert roi_edit(19641, slave_roi, token)['accepted'] is False, 'cross-node ROI owner forged'
        post_master = core_snapshot(19621)
        post_slave = core_snapshot(19622)
        assert len(post_master['roiDrafts']) == len(post_slave['roiDrafts']) == 1
        assert post_master['roiDrafts'][0]['channelId'] == 'CH06'
        assert post_slave['roiDrafts'][0]['channelId'] == 'CH02'
        assert post_master['roiDrafts'][0]['revision'] == 1
        assert post_master['roiDrafts'][0]['x'] == 0.15
        assert post_slave['roiDrafts'][0]['revision'] == 1
        assert scalar(evidence / 'master/sim-config.sqlite',
                      "SELECT revision FROM sim_roi_drafts WHERE channel_id='CH06'") == 1
        assert scalar(evidence / 'slave/sim-config.sqlite',
                      "SELECT revision FROM sim_roi_drafts WHERE channel_id='CH02'") == 1
        assert scalar(evidence / 'master/sim-config.sqlite',
                      'SELECT COUNT(*) FROM sim_roi_drafts') == 1
        assert scalar(evidence / 'slave/sim-config.sqlite',
                      'SELECT COUNT(*) FROM sim_roi_drafts') == 1
        assert post_master.get('activeProduct') == snapshot.get('activeProduct'), 'ROI draft changed active product'
        assert post_master.get('production') == snapshot.get('production'), 'ROI draft changed production final'
        print('SIM_ROI_EDIT_E2E_PASS accepted=2 stale=1 bad_token=1 foreign=1 '
              'master_ipc=1 slave_ipc=1 no_activation=1', flush=True)

        if args.repeat_barcode:
            barcodes = query(evidence / 'master/production.db', 'SELECT barcode FROM workpieces')
            assert len({x[0] for x in barcodes}) == 1, 'Duplicate barcode test was not exercised'
        if args.with_images:
            slave_log = (evidence / 'slave.log').read_text(encoding='utf-8')
            if args.legacy_shadow:
                assert master_log.count('SIM_PHYSICAL_CAPTURE_PASS') == args.count * 4
                assert slave_log.count('SIM_PHYSICAL_CAPTURE_PASS') == args.count * 5
                assert master_log.count('SIM_ST05_TRIGGERED_CAPTURE_PASS') == args.count
                assert master_log.count('SIM_ST05_REAL_FINS_CAM_DONE_PASS') == args.count
                assert master_log.count('SIM_MASTER_SLAVE_TRIGGER_QUEUED') == args.count * 5
                assert slave_log.count('SIM_SLAVE_TRIGGER_COMPLETED') == args.count * 5
            else:
                assert master_log.count('SIM_IMAGE_REAL_IMPORTED') == args.count
                assert slave_log.count('SIM_IMAGE_REAL_IMPORTED') == args.count
                assert 'defective=1' in master_log
                assert 'defective=0' in slave_log
            print('SIM_REAL_IMAGE_E2E_PASS actual_png=9 per_item_channels=11 '
                  'line_parity=2 contour_ng=1 per_item_REJECT=1', flush=True)
        result = 'HOLD' if args.flip_token else 'REJECT' if args.with_images else 'GOOD'
        print(f'SIM_FULL_E2E_PASS items={args.count} finals={target} outcome={result} '
              f'master_channels={args.count * 11} slave_channels={args.count * 6} '
              f'ack={args.count * (12 if args.legacy_shadow else 7)} fifo=STRICT max_inflight={max(observed)} false_release=0 UI=2',
              flush=True)
        return 0
    except Exception as exc:
        print('SIM_FULL_E2E_FAIL ' + repr(exc), file=sys.stderr)
        for name, _ in processes:
            path = evidence / (name + '.log')
            if path.is_file():
                print(name + ':\n' + path.read_text(encoding='utf-8', errors='replace')[-2400:],
                      file=sys.stderr)
        return 1
    finally:
        for _, proc in reversed(processes):
            if proc.poll() is None:
                proc.terminate()
        for _, proc in processes:
            try: proc.wait(timeout=4)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait()
        for stream in logs:
            stream.close()
        print('SIM_FULL_EVIDENCE=' + str(evidence), flush=True)


if __name__ == '__main__':
    sys.exit(main())
