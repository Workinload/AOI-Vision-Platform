#!/usr/bin/env python3
"""A10 independent real Draft202012Validator check. No hardware, C01 remains DRAFT."""
from __future__ import annotations
import argparse
import copy
import hashlib
import json
from pathlib import Path

from jsonschema import Draft202012Validator


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--schema", required=True, type=Path)
    args = p.parse_args()
    data = args.schema.read_bytes()
    schema = json.loads(data)
    Draft202012Validator.check_schema(schema)
    validate = Draft202012Validator(schema)

    valid = {
        "SchemaVersion": "C01-DRAFT-0", "MessageId": "sha-review-1",
        "PayloadHash": "a" * 64, "WorkpieceId": "wp-12", "Epoch": 2,
        "ReleaseId": "r-draft", "ChannelId": "CH08",
        "CaptureId": "capture-11", "AttemptNo": 1,
        "ProcessingStatus": "SUCCEEDED", "QualityStatus": "NG",
    }
    def check(item, expected_valid, label):
        errors = sorted((str(e.message) for e in validate.iter_errors(item)))
        require((not errors) == expected_valid,
                f"{label}: expected_valid={expected_valid} errors={errors[:3]}")
        print("SCHEMA_CASE", label, "ACCEPT" if not errors else "REJECT", len(errors))

    check(valid, True, "successful_processing_NG")
    check(dict(valid, QualityStatus="OK"), True, "successful_processing_OK")
    check(dict(valid, ProcessingStatus="ERROR", QualityStatus="UNDECIDED"),
          True, "error_and_undecided")
    statuses = ("PENDING", "RUNNING", "ERROR", "CANCELLED", "TIMEOUT")
    for status in statuses:
        check(dict(valid, ProcessingStatus=status, QualityStatus="OK"),
              False, "non_success_" + status + "_must_reject_OK")
    check(dict(valid, QualityStatus="OK", ChannelId="CH12"), False, "unknown_channel")
    check(dict(valid, QualityStatus="OK", PayloadHash="invalid"), False, "invalid_hash")
    check(dict(valid, UnknownField="injected"), False, "unexpected_property")
    check({k:v for k,v in valid.items() if k != "WorkpieceId"},
          False, "missing_WorkpieceId")

    witness = dict(valid, ProcessingStatus="ERROR", QualityStatus="OK")
    mutations = (
        ("remove_allOf", lambda d: d.pop("allOf", None)),
        ("weaken_then_guard", lambda d: d["allOf"][0]["then"]
         ["properties"]["QualityStatus"]["not"].__setitem__("const", "UNKNOWN")),
        ("wrong_if_processing_enum", lambda d: d["allOf"][0]["if"]
         ["properties"]["ProcessingStatus"].__setitem__("enum", ["SUCCEEDED"])),
    )
    for name, mutate in mutations:
        modified = copy.deepcopy(schema)
        mutate(modified)
        Draft202012Validator.check_schema(modified)
        now_accepts = not list(Draft202012Validator(modified).iter_errors(witness))
        require(now_accepts, name + " not detected by witness")
        print("SCHEMA_MUTATION_CAUGHT", name,
              "BAD_SCHEMA_WOULD_ACCEPT_ERROR_PLUS_OK")
    print("A10_C01_JSON_SCHEMA_RUNTIME_PASS",
          "draft2020_12=TRUE", "positive_cases=3",
          "negative_cases=9", "mutations=3",
          "schema_sha256=" + hashlib.sha256(data).hexdigest(),
          "C01_NOT_FROZEN=TRUE")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
