#!/usr/bin/env python3
"""A0 Host E2E: external PLC-supplied unreadable barcode -> reverse FINS HOLD.

Five real OS processes (vPLC + Master/Slave Core + two Avalonia UIs).
The virtual PLC, NOT the Host, injects unreadable D2020 words for token 3.
A0 must persist that exact WorkpieceId HOLD before sending actual FINS/UDP
D2099=3 followed by D1016=2, with independent acknowledgements and readbacks.
Tokens 1 and 2 must still complete legitimate REJECT exits; token 3 cannot exit.
"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time

from e2e_sim_full import core_snapshot, fins_read, make_png, query, scalar


def verify(root):
    mdb = root / 'master/production.db'
    sdb = root / 'slave/production.db'
    ledger = root / 'master/sim-full-cycle.db'
    assert scalar(mdb, 'PRAGMA integrity_check') == 'ok'
    assert scalar(sdb, 'PRAGMA integrity_check') == 'ok'
    items = query(ledger,
        'SELECT token,identity_json,phase,disposition FROM sim_full_tokens ORDER BY token')
    assert len(items) == 3 and [i[0] for i in items] == [1, 2, 3], items
    assert [i[2] for i in items] == ['exited', 'exited', 'registered'], items
    assert [i[3] for i in items] == ['Reject', 'Reject', None], items
    blocked = json.loads(items[2][1])['workpieceId']
    assert scalar(mdb, 'SELECT COUNT(*) FROM workpieces WHERE final_json IS NOT NULL') == 2
    assert scalar(mdb, 'SELECT COUNT(*) FROM channel_results') == 33
    assert scalar(sdb, 'SELECT COUNT(*) FROM channel_results') == 18
    hold = query(mdb,
        'SELECT workpiece_id,reason_code,detail FROM workpiece_holds')
    assert len(hold) == 1 and hold[0][0] == blocked and (
        hold[0][1] == 'END_BARCODE_SCAN_FAILED'), hold
    assert 'manual reconciliation' in hold[0][2]
    assert scalar(mdb, "SELECT COUNT(*) FROM workpieces WHERE workpiece_id='" +
                  blocked + "' AND final_json IS NOT NULL") == 0
    assert fins_read(4005) == 2, 'Faulty item was physically ejected'
    assert fins_read(4002) == 3, 'Faulty item is no longer FIFO head'
    assert fins_read(2099) == 3 and fins_read(1016) == 2

    wire = root / 'p0-fins-wire.jsonl'
    assert wire.is_file(), 'Missing independently recorded real FINS UDP wire'
    packets = [json.loads(row) for row in wire.read_text(encoding='utf-8').splitlines()
               if row.strip()]
    def written(token, address, value):
        return [p for p in packets if p['Token'] == token and p['Address'] == address
                and p['IsWrite'] and p['Values'] == [value] and p['Outcome'] == 'OK'
                and p['ResponseHex'].startswith('C0')]
    for token in (1, 2):
        a = written(token, 1016, 2)
        b = written(token, 2099, 2)
        assert len(a) == len(b) == 1 and a[0]['Sequence'] < b[0]['Sequence'], (
            'Healthy predecessor final sequence invalid', token)
    fault = [p for p in packets if p['Token'] == 3 and p['Address'] == 2020 and
             not p['IsWrite'] and p['Outcome'] == 'INJECTED_END_SCAN_UNREADABLE']
    assert fault, 'External PLC never supplied actual unreadable D2020 reply'
    assert all(bytes.fromhex(p['ResponseHex'])[14:] == bytes(20) for p in fault), (
        'External unreadable scanner input is not 10 actual zero FINS words')
    offers = [p for p in packets if p['Token'] == 3 and p['Address'] == 4303 and
              p['IsWrite'] and p['Values'] == [3, 1] and p['Outcome'] == 'OK']
    assert len(offers) == 1, 'Missing real FINS external scanner failure offer'
    dm2100 = [p for p in packets if p['Token'] == 3 and p['Address'] == 2100 and
              not p['IsWrite'] and p['Outcome'] == 'OK']
    assert dm2100 and all(bytes.fromhex(p['ResponseHex'])[14:] == bytes(20)
                          for p in dm2100), (
                          'Host did not observe actual empty D2100 scanner words')
    first = written(3, 2099, 3)
    second = written(3, 1016, 2)
    assert len(first) == len(second) == 1 and (
        offers[0]['Sequence'] < first[0]['Sequence'] < second[0]['Sequence'])
    assert any(p['Token'] == 3 and p['Address'] == 2099 and
               not p['IsWrite'] and first[0]['Sequence'] < p['Sequence'] <
               second[0]['Sequence'] and p['Outcome'] == 'OK' for p in packets), (
               'Missing first FINS confirmation readback')
    assert any(p['Token'] == 3 and p['Address'] == 1016 and
               not p['IsWrite'] and p['Sequence'] > second[0]['Sequence'] and
               p['Outcome'] == 'OK' for p in packets), (
               'Missing second FINS confirmation readback')
    assert not written(3, 2099, 2), 'Failed scan was wrongly acknowledged normally'
    assert not any(p['Token'] == 3 and p['IsWrite'] and
                   p['Address'] == 4003 for p in packets), (
                   'Faulty workpiece was physically ejected despite HOLD')
    master = (root / 'master.log').read_text(encoding='utf-8')
    assert 'SIM_LEGACY_ABNORMAL_FINS_PASS token=3 scenario=BarcodeFailure' in master
    assert 'SIM_FULL_HOLD token=3' in master
    assert master.count('SIM_FULL_EXITED') == 2
    for port in (19621, 19622):
        snapshot = core_snapshot(port)
        assert snapshot['realHardwareWritesEnabled'] is False
    print('A0_EXTERNAL_END_SCAN_FAILURE_E2E_PASS items=3 finals=2 ' +
          'actual_D2020_unreadable=1 actual_D2100_empty=1 ' +
          'real_FINS_D2099_3_then_D1016_2=1 both_readbacks=1 ' +
          'workpiece_hold=1 physical_exit_blocked=1 ' +
          'master_11channels=33 slave_6channels=18 ' +
          'other_parts_unaffected=2 raw_fins_packets=1 ' +
          'real_hardware_writes=0', flush=True)


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--evidence-root')
    p.add_argument('--dotnet', default='dotnet')
    p.add_argument('--timeout', type=float, default=50)
    args = p.parse_args()
    source = Path(__file__).resolve().parent.parent
    host = source / 'src/AOI.Host/bin/Debug/net10.0/AOI.Host.dll'
    ui = source / 'src/AOI.Ui.Avalonia/bin/Debug/net10.0/AOI.Ui.Avalonia.dll'
    assert host.is_file() and ui.is_file(), 'Build AOI.slnx -c Debug first'
    root = (Path(args.evidence_root).resolve() if args.evidence_root else
            Path(tempfile.mkdtemp(prefix='aoi-scan-failure-')))
    if root.exists() and any(root.iterdir()):
        raise RuntimeError('Cannot reuse existing fault evidence root')
    root.mkdir(parents=True, exist_ok=True)

    imgs = root / 'operator-images'
    for channel in range(1, 8):
        make_png(imgs / f'CH{channel:02d}.png', bright_defect=channel == 6)
    for camera in ('LINE-S-8', 'LINE-M-2'):
        make_png(imgs / (camera + '.png'), line_parent=True)

    env = dict(os.environ)
    env.update({
        'AOI_SIM_MODE': 'SIM_FULL',
        'AOI_SIM_CHANNEL_SET': 'ALL_11',
        'AOI_SIM_ITEM_COUNT': '3',
        'AOI_SIM_MAX_IN_FLIGHT': '3',
        'AOI_SIM_END_BARCODE_FLIP_TOKEN': '0',
        'AOI_SIM_END_SCAN_FAIL_TOKENS': '3',
        'AOI_SIM_REPEAT_BARCODE': '0',
        'AOI_SIM_PLC_PROFILE': 'LEGACY_PLC_SHADOW',
        'AOI_SIM_IMAGE_ROOT': str(imgs),
        'AOI_SIM_IMAGE_LAYOUT': 'FLAT',
        'AOI_SIM_DROP_PEER_AFTER_SLAVE_REGISTRATION': '0',
        'AOI_SIM_CONFIG_TOKEN': 'SIM_ONLY_E2E_ROI_OPERATOR_TOKEN_2026',
        'DOTNET_CLI_TELEMETRY_OPTOUT': '1'
    })
    processes, handles = [], []
    def start(name, dll, *flags):
        log = (root / (name + '.log')).open('w', encoding='utf-8')
        handles.append(log)
        proc = subprocess.Popen([args.dotnet, str(dll), *flags], env=env,
                                cwd=source, stdout=log, stderr=subprocess.STDOUT)
        processes.append((name, proc))
        print('A0_END_SCAN_PROCESS_STARTED name=' + name +
              ' pid=' + str(proc.pid), flush=True)

    try:
        start('vplc', host, 'vplc', '--data', str(root))
        start('slave', host, 'slave', '--data', str(root))
        start('master', host, 'master', '--data', str(root))
        start('master-ui', ui, '--master', '--headless', '--once')
        start('slave-ui', ui, '--slave', '--headless', '--once')
        deadline = time.monotonic() + args.timeout
        while time.monotonic() < deadline:
            for name, proc in processes[:3]:
                if proc.poll() is not None:
                    raise RuntimeError(name + ' stopped unexpectedly; rc=' +
                                       str(proc.returncode))
            if (scalar(root / 'master/production.db',
                       'SELECT COUNT(*) FROM workpieces WHERE final_json IS NOT NULL') == 2
                and scalar(root / 'master/production.db',
                           'SELECT COUNT(*) FROM channel_results') == 33
                and scalar(root / 'slave/production.db',
                           'SELECT COUNT(*) FROM channel_results') == 18
                and scalar(root / 'master/production.db',
                           'SELECT COUNT(*) FROM workpiece_holds') == 1
                and fins_read(4005) == 2 and fins_read(4002) == 3
                and fins_read(2099) == 3 and fins_read(1016) == 2):
                break
            time.sleep(0.15)
        else:
            raise TimeoutError('External scanner failed to drive exact two-step FINS HOLD')
        # The raw UDP recorder runs independently. Give its async flush a bounded
        # chance to catch up with the already-completed original PLC datagrams.
        time.sleep(0.5)
        verify(root)
        for name, proc in processes[:3]:
            assert proc.poll() is None, name + ' Core exited on scanner fault'
        for name, proc in processes[3:]:
            assert proc.wait(timeout=10) == 0, name + ' GUI snapshot failed'
            assert 'UI_HEADLESS_SNAPSHOT_OK' in (
                root / (name + '.log')).read_text(encoding='utf-8')
        return 0
    except Exception as exc:
        print('A0_EXTERNAL_END_SCAN_FAILURE_E2E_FAIL ' + repr(exc),
              file=sys.stderr)
        for name, _ in processes:
            f = root / (name + '.log')
            if f.is_file():
                print(name + ':\n' +
                      f.read_text(encoding='utf-8', errors='replace')[-3200:],
                      file=sys.stderr)
        return 1
    finally:
        for _, proc in reversed(processes):
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
        print('A0_EXTERNAL_END_SCAN_EVIDENCE=' + str(root), flush=True)


if __name__ == '__main__':
    sys.exit(main())
