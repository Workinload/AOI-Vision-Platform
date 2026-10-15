#!/usr/bin/env python3
"""A10 read-only provenance/traceability audit. No TC is executed by this audit.

Source ZIP is mandatory to validate provenance; selected v2 reference files are
checked byte-for-byte against the exact locked original, not just against copies.
"""
from __future__ import annotations
import argparse
from collections import Counter
import csv
from hashlib import sha256
import io
import json
from pathlib import Path
import re
import sys
import zipfile


def digest(path: Path) -> str:
    h = sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()


def records(data: bytes, field: str) -> list[dict[str, str]]:
    rows = list(csv.DictReader(io.StringIO(data.decode('utf-8-sig'))))
    check(bool(rows), f'{field}: source list is empty')
    return rows


def unique(rows: list[dict[str, str]], field: str, size: int) -> dict[str, dict[str, str]]:
    index = {x[field]: x for x in rows}
    check(len(index) == size == len(rows), f'{field}: expected {size} unique, got {len(index)} / {len(rows)}')
    return index


def ids(value: str) -> set[str]:
    return set(filter(None, re.split(r'[;,\s]+', value.strip())))


def check(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', type=Path, required=True)
    parser.add_argument('--spec-zip', type=Path, required=True)
    parser.add_argument('--template-zip', type=Path)
    parser.add_argument('--sdk-manifest', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    opts = parser.parse_args()
    root = opts.repo.resolve()
    lock = json.loads((root / 'BASELINE_LOCK.json').read_text(encoding='utf-8'))
    release = json.loads((root / 'RELEASE.json').read_text(encoding='utf-8'))
    ledger = json.loads((root / 'docs/agents/TASK_LEDGER.json').read_text(encoding='utf-8'))
    observed: dict[str, object] = {'audit': 'AOI-Agent05', 'base': release['release_id'],
                                   'locked_source_baseline': lock['baseline'],
                                   'mode': 'SIM_ONLY', 'test_execution': 'NOT_EXECUTED_BY_THIS_AUDIT'}
    # The immutable v2 source lock remains M00-r001 after a governance-only release bump.
    # This audit must support later source releases without weakening the original hash lock.
    check(lock['baseline'] == 'M00-r001' and re.fullmatch(r'M00-r\d{3}', release['release_id']),
          'unexpected locked source baseline or release ID')
    check(lock['contract_version'] == release['contract_version'] == 'C00-DRAFT', 'contract lock mismatch')
    check(lock['build_mode'] == release['mode'] == 'SIM_ONLY', 'production build mode forbidden')
    check(release['sim_ready'] is False and release['field_gate_status'] == 'FIELD_PENDING_ALL_14',
          'unsafe release certification')
    w001 = ledger['items']['W001']
    check(w001['status'] in {'VERIFIED', 'IMPLEMENTED_PENDING_A10_REVIEW'},
          'W001 ledger has unknown acceptance status')
    if w001['status'] == 'VERIFIED':
        check(w001.get('scope') == 'SOURCE_PROVENANCE_ONLY' and w001.get('approver') == 'A0',
              'VERIFIED W001 is not limited to A0 source provenance')
        check((root / 'docs/reviews/A0_W001_SOURCE_APPROVAL_20261015.md').is_file(),
              'VERIFIED W001 lacks approval document')
    
    for key, path in (('spec_v2_zip', opts.spec_zip),
                      ('qt_reference_template_zip', opts.template_zip),
                      ('sdk_parts_manifest', opts.sdk_manifest)):
        if path is None:
            continue
        expected = lock['sources'][key]
        check(path.is_file(), f'{key} missing: {path}')
        actual = digest(path)
        check(path.stat().st_size == expected['size'] and actual == expected['sha256'],
              f'{key} hash/size conflict')
        observed[key] = {'sha256': actual, 'size': path.stat().st_size, 'verified': True}
    spec_ref = root / 'reference/spec_v2_machine_readable'
    spec_file_count = 0
    with zipfile.ZipFile(opts.spec_zip) as archive:
        check(archive.testzip() is None, 'original v2 spec ZIP CRC failed')
        prefix = 'AOI_新一代上位机_开发设计全书_v2.0_20261015/07_可机读清单/'
        for filename, expected in lock['reference_files'].items():
            original_name = prefix + filename
            check(original_name in archive.namelist(), f'missing original source file: {filename}')
            original = archive.read(original_name)
            snap = (spec_ref / filename).read_bytes()
            check(sha256(snap).hexdigest() == expected, f'embedded source digest mismatch: {filename}')
            check(original == snap, f'embedded source is NOT identical to v2 original: {filename}')
            spec_file_count += 1
    observed['v2_embedded_files_byte_identical'] = spec_file_count

    def from_spec(name: str) -> list[dict[str, str]]:
        return records((spec_ref / name).read_bytes(), name)

    def from_plan(name: str) -> list[dict[str, str]]:
        return records((root / 'reference/plan_v1.1' / name).read_bytes(), name)

    req = unique(from_spec('requirements_trace.csv'), 'RequirementId', 114)
    cases = unique(from_spec('acceptance_cases.csv'), 'TestCaseId', 114)
    coverage = unique(from_plan('RQ_114_COVERAGE.csv'), 'RequirementId', 114)
    tasks = unique(from_plan('WBS_TASKS_130.csv'), 'TaskId', 130)
    modules = unique(from_spec('module_registry.csv'), 'ModuleId', 21)
    plc = unique(from_spec('plc_signal_registry.csv'), 'SignalId', 45)
    gates = unique(from_spec('field_verification_gates.csv'), 'GateId', 14)
    channels = unique(from_spec('station_channel_map.csv'), 'ChannelId', 11)
    scenarios = unique(from_plan('SCENARIO_MATRIX.csv'), 'ScenarioId', 52)
    check(set(req) == set(coverage), '114 requirements not represented in PLAN coverage')
    check(Counter(row['Priority'] for row in req.values()) == {'P0': 78, 'P1': 36},
          'incorrect 78 P0 + 36 P1 priorities')
    check(Counter(row['Priority'] for row in coverage.values()) == {'P0': 78, 'P1': 36},
          'PLAN priorities not 78/36')
    check(Counter(row['Priority'] for row in cases.values()) == {'P0': 78, 'P1': 36},
          'test case priorities not 78/36')
    for identifier, requirement in req.items():
        plan = coverage[identifier]
        check(requirement['Priority'] == plan['Priority'], f'{identifier} priority mismatch')
        test_ids = ids(requirement['TestCaseIds'])
        check(bool(test_ids) and test_ids == ids(plan['TestCaseIds']),
              f'{identifier} TC links differ across locked v2 and PLAN')
        check(all(tc in cases for tc in test_ids), f'{identifier} missing test case')
        check(all(cases[tc]['RequirementId'] == identifier for tc in test_ids),
              f'{identifier} test case points to a different requirement')
        check(all(cases[tc]['Priority'] == requirement['Priority'] for tc in test_ids),
              f'{identifier} test priority mismatch')
        check(bool(plan['PrimaryWBS']) and plan['PrimaryWBS'] in tasks,
              f'{identifier} primary WBS missing')
        check(ids(plan['SupportingWBS']).issubset(set(tasks)), f'{identifier} support WBS missing')
    check(all(row['RequirementId'] in req for row in cases.values()),
          'one or more tests without a valid requirement')
    check(all(row['ExecutionStatus'] in {'NOT_RUN', 'NOT_EXECUTED'} for row in cases.values()),
          'false positive executed TC state')
    check(all(row['Status'] == 'NOT_STARTED' for row in tasks.values()),
          'immutable baseline task plan was unexpectedly modified')
    check(all(row['ExecutionStatus'] in {'NOT_RUN', 'NOT_EXECUTED'} for row in scenarios.values()),
          'scenario matrix claims unexpected execution')
    check(len({row['PhysicalDeviceId'] for row in channels.values()}) == 9,
          'physical device count is not 9')
    check(set(channels) == {f'CH{i:02}' for i in range(1, 12)},
          'missing or duplicate CH01..CH11')
    check(all(x['ProductionWriteEnabled'].strip().lower() in {'false', 'no', '0'} for x in plc.values()),
          'production PLC write flag enabled')
    check(all(x['ProductionWriteAllowed'].strip().lower() in {'false', 'no', '0'} for x in gates.values()),
          'field gate production write allowed')
    check(all(not x['FieldSignoff'].strip() for x in gates.values()),
          'field signoff unexpectedly filled')
    check(Counter(x['Status'] for x in gates.values()) == {'BLOCKED': 7, 'OPEN': 7},
          'field gate statuses altered')
    observed['counts'] = {
        'RQ': len(req), 'P0': 78, 'P1': 36, 'TC': len(cases),
        'WBS': len(tasks), 'MODULE': len(modules), 'PLC': len(plc),
        'FG': len(gates), 'DEVICE': 9, 'CHANNEL': len(channels), 'SCENARIO': len(scenarios)}
    observed['statuses'] = {'TC': dict(Counter(r['ExecutionStatus'] for r in cases.values())),
                            'FG': dict(Counter(r['Status'] for r in gates.values())),
                            'W001_ledger': ledger['items']['W001']['status'],
                            'W005_ledger': ledger['items']['W005']['status'],
                            'W007_ledger': ledger['items']['W007']['status']}
    observed['results'] = {
        'W001_provenance': 'A0_APPROVED_SOURCE_ONLY_CANDIDATE' if w001['status'] == 'VERIFIED'
                           else 'REVIEW_EVIDENCE_PASS_A0_APPROVAL_REQUIRED',
        'W005_mapping': 'TRACE_LINKS_VALIDATED_ONLY_NO_FUNCTIONAL_TC',
        'W007_windows_sdk_abi': 'NOT_VERIFIED',
        'functional_114_tc': 'NOT_EXECUTED',
        'field_14_fg': 'FIELD_PENDING'}
    opts.output.parent.mkdir(parents=True, exist_ok=True)
    opts.output.write_text(json.dumps(observed, indent=2, ensure_ascii=False, sort_keys=True) + '\n',
                           encoding='utf-8')
    print('PASS A10 read-only audit: original ZIP SHA+CRC+14 byte-identical refs;'
          ' 114 RQ -> 114 TC -> 130 WBS, 78 P0/36 P1; 21 MOD/45 PLC/14 FG/9 devices/11 channels')
    print('W001:', w001['status'], '(source provenance only); W005 mapping is static, '
          'Windows native and 114 functional TC remain NOT_VERIFIED')
    print('REPORT=' + str(opts.output.resolve()))
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (ValueError, KeyError, OSError, zipfile.BadZipFile) as exc:
        print('FAIL A10 provenance/trace audit: ' + str(exc), file=sys.stderr)
        sys.exit(1)
