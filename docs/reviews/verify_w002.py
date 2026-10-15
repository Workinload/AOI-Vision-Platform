#!/usr/bin/env python3
"""W002 exact v2 locked fact crosswalk, read-only field gates, mutation negatives.

Usage: python3 docs/reviews/verify_w002.py --repo-root . --negative
Source: immutable v2 machine readable reference, not a live PLC configuration.
"""
import argparse
import csv
import hashlib
import io
import json
from collections import Counter
from pathlib import Path

SIGNAL_FIELDS = (
    "SignalId", "PLCAddress", "StationId", "HostRole", "Direction",
    "DataType", "EffectiveWords", "PurposeCN", "RiskNote", "EvidenceSource",
    "ValidationState", "ConflictGroup", "ProductionWriteEnabled",
)
GATE_FIELDS = (
    "GateId", "Topic", "Status", "SourceConflict", "RequiredEvidence",
    "ProposedOwner", "ProductionWriteAllowed", "FieldSignoff",
)
CONFLICTS = {
    "D1030": "SIG_OLD_UI_X",
    "D1000": "SIG_OLD_PLC_X",
    "D1013": "SIG_CAM_REAR_LINE",
    "D2000": "SIG_BARCODE_WORDS",
    "D1016": "SIG_FINAL_QUALITY",
    "D2099": "SIG_DOWNSTREAM_REQUEST",
}

def load(path):
    raw = path.read_bytes()
    rows = list(csv.DictReader(io.StringIO(raw.decode("utf-8-sig"))))
    if not rows:
        raise AssertionError(f"missing rows: {path}")
    return rows

def assert_unique(rows, key, expected):
    ids = [r[key] for r in rows]
    assert len(ids) == expected, (key, len(ids), expected)
    assert len(set(ids)) == expected, (key, "duplicates")
    return {r[key]: r for r in rows}

def verify(signals, master_signals, gates, master_gates, plan_gates):
    a = assert_unique(master_signals, "SignalId", 45)
    b = assert_unique(signals, "SignalId", 45)
    assert set(a) == set(b), "signal ids drift"
    assert all(set(SIGNAL_FIELDS) <= set(r) for r in signals), "missing signal columns"
    for key in a:
        for col in SIGNAL_FIELDS:
            assert b[key][col] == a[key][col], ("signal drift", key, col)
        assert b[key]["ProductionWriteEnabled"] == "FALSE", ("unsafe signal", key)
    assert all(r.get("ActivationAllowed", "FALSE") != "TRUE" for r in master_signals), "field activation unlocked"
    a = assert_unique(master_gates, "GateId", 14)
    b = assert_unique(gates, "GateId", 14)
    c = assert_unique(plan_gates, "GateId", 14)
    assert set(a) == set(b) == set(c), "FG id drift"
    assert Counter(r["Status"] for r in gates) == {"BLOCKED": 7, "OPEN": 7}, "FG statuses drift"
    for key in a:
        for col in GATE_FIELDS:
            assert b[key][col] == a[key][col], ("FG source drift", key, col)
        assert b[key]["ProductionWriteAllowed"] == "FALSE", ("unsafe FG", key)
        assert not b[key]["FieldSignoff"].strip(), ("unsigned gate marked signed", key)
        assert c[key]["ProductionWriteAllowed"] == "FALSE", ("PLAN FG unlock", key)
    signal_map = assert_unique(signals, "SignalId", 45)
    for addr, sid in CONFLICTS.items():
        assert sid in signal_map and signal_map[sid]["PLCAddress"].startswith(addr), (addr, sid)
    assert len([r for r in signals if r["PLCAddress"] == "D1013"]) >= 2, "D1013 alias removed"
    assert len([r for r in signals if r["PLCAddress"] == "D1000"]) >= 2, "D1000 alias removed"
    return {"signals": 45, "field_gates": 14, "blocked": 7, "open": 7, "critical_conflicts": len(CONFLICTS), "production_write_allowed": 0}

def mutation_negatives(s, ms, g, mg, pg):
    import copy
    tests = [
        ("signal_address_tampered", lambda v: v[0][0].__setitem__("PLCAddress", "D9999")),
        ("signal_write_enabled", lambda v: v[0][0].__setitem__("ProductionWriteEnabled", "TRUE")),
        ("fg_write_unlocked", lambda v: v[2][0].__setitem__("ProductionWriteAllowed", "TRUE")),
        ("fake_field_signoff", lambda v: v[2][0].__setitem__("FieldSignoff", "FAKE")),
        ("field_blocker_erased", lambda v: v[2][0].__setitem__("Status", "OPEN")),
        ("D1013_alias_removed", lambda v: v[0].__setitem__(next(i for i,r in enumerate(v[0]) if r["SignalId"]=="SIG_CAM_REAR_LINE_CLEAR_ALIAS"),dict(next(r for r in v[0] if r["SignalId"]=="SIG_CAM_REAR_LINE_CLEAR_ALIAS"), PLCAddress="D1014"))),
    ]
    for name, alter in tests:
        copy_data = copy.deepcopy([s, ms, g, mg, pg])
        alter(copy_data)
        try:
            verify(*copy_data)
        except AssertionError:
            print("MUTATION_REJECTED", name)
        else:
            raise AssertionError("mutation falsely passed " + name)
    return len(tests)

def main():
    p = argparse.ArgumentParser()
    p.add_argument("--repo-root", default=".")
    p.add_argument("--negative", action="store_true")
    a = p.parse_args()
    root = Path(a.repo_root)
    files = [
        root/"docs/reviews/W002_STATIC_FACTS_45.csv",
        root/"reference/spec_v2_machine_readable/plc_signal_registry.csv",
        root/"docs/reviews/W002_FIELD_GATE_OPEN_14.csv",
        root/"reference/spec_v2_machine_readable/field_verification_gates.csv",
        root/"docs/plan/v1.1-original/FIELD_GATES_14.csv",
    ]
    rows = [load(x) for x in files]
    result = verify(*rows)
    result["mutation_negative_rejections"] = mutation_negatives(*rows) if a.negative else None
    result["source_sha256"] = {str(x.relative_to(root)):hashlib.sha256(x.read_bytes()).hexdigest() for x in files}
    result["status"] = "STATIC_EVIDENCE_PASS_A10_REVIEW_REQUIRED_NOT_FIELD_ACCEPTED"
    print(json.dumps(result,ensure_ascii=False,sort_keys=True,indent=2))
    return 0

if __name__ == "__main__":
    raise SystemExit(main())