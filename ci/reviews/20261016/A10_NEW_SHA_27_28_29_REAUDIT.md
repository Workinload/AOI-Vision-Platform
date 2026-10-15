# A10 independent re-audit — exact new SHA, 2026-10-16

Repository: Workinload/AOI-Vision-Platform (PRIVATE); main `ecd8373e9b35f7867678160fbd0fbf772fa43b0b`. PR #12 merged, no A10 merge performed.

## Exact head outcomes

| PR | Pinned SHA | Independent round 1 | Round 2 | Negative tests | Decision |
|---|---|---|---|---|---|
| #27 W002 | `c0a3c66421e269a5ca22583cfa6e2fe1eb9b14b0` | exit 0 | exit 0 | 6/6 each | PASS_STATIC_ONLY |
| #28 W003 | `a88ec22ea05a8d191b96139596e675058c373c45` | exit 1 | exit 1 | blocked by DAG header | HOLD |
| #29 C01 Draft | `d3e73167d903181ebb7fe010cbd74561ff61b0d0` | exit 0 | exit 0 | 7/7 each + runtime 3/9/3 | PASS_DRAFT_ONLY / FREEZE_HOLD |

Source-of-truth real Actions logs, job exit codes and attached job artifacts: https://github.com/Workinload/AOI-Vision-Platform/actions/runs/38772323194

## Commands actually run by GitHub Actions
```sh
python3 ci/a10_pinned_upstream_review.py --repo /tmp/a10-upstream --pr 27 --sha c0a3c66421e269a5ca22583cfa6e2fe1eb9b14b0 --round 1
python3 ci/a10_pinned_upstream_review.py --repo /tmp/a10-upstream --pr 27 --sha c0a3c66421e269a5ca22583cfa6e2fe1eb9b14b0 --round 2
python3 docs/reviews/verify_w002.py --repo-root . --negative
python3 ci/a10_pinned_upstream_review.py --repo /tmp/a10-upstream --pr 28 --sha a88ec22ea05a8d191b96139596e675058c373c45 --round 1
python3 ci/a10_pinned_upstream_review.py --repo /tmp/a10-upstream --pr 28 --sha a88ec22ea05a8d191b96139596e675058c373c45 --round 2
python3 docs/reviews/verify_w003.py --repo-root . --negative
python3 docs/reviews/verify_w003_owner.py .
python3 ci/a10_pinned_upstream_review.py --repo /tmp/a10-upstream --pr 29 --sha d3e73167d903181ebb7fe010cbd74561ff61b0d0 --round 1
python3 ci/a10_pinned_upstream_review.py --repo /tmp/a10-upstream --pr 29 --sha d3e73167d903181ebb7fe010cbd74561ff61b0d0 --round 2
python3 contracts/C01/test_c01_draft_contract.py
python3 -m pip install --user jsonschema==4.23.0
python3 ci/a10_c01_runtime_schema.py --schema /tmp/a10-upstream/contracts/C01/CORRELATION_ENVELOPE_DRAFT.schema.json
```

Pinned worktrees pass `git rev-parse HEAD == required SHA`. Each round checks all 55 locked PLAN file SHA256, 130 WBS / 114 unique RQ-TC, 14 FG production write FALSE, 114 TC NOT_RUN.

W002: real source fact/FG crosscheck and mutation tests passed. This is static, NOT real PLC acceptance.

W003: 21-owner mapping standalone script exit 0, but proposed `W003_MODULE_DAG_CANDIDATE.csv` STILL has an extraction prefix instead of normal CSV header, causing `KeyError: ModuleId` in both rounds (exit 1). 30-edge DAG validation and negatives cannot be certified.

C01: upstream draft unittest exit 0 twice; 7 independent negatives caught in both rounds; actual `jsonschema.Draft202012Validator` accepted 3 valid examples, rejected 9 invalid examples including PENDING/RUNNING/ERROR/CANCELLED/TIMEOUT + OK, and detected 3 rule weakening mutations. Draft schema SHA256 `2110f2b245ff0b43b2f41bd38c6f5efea9a0fa9e14227b5a2d65ef96db7f70c6`.

C01 is NOT_FROZEN, W004 not VERIFIED, A0/consumers must review. All three PRs still Draft; never auto-merge. A10 test code is A0-review-required.

Evidence files are also archived under `/AOI开发成果/A10_NEW_SHA_REAUDIT_20261016.md`.