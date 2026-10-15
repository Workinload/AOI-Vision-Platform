# W002 - A0 legacy fact crosswalk and field-gate LOCK (Issue #6)

**Review request**: DRAFT / STATIC_PREP_PASS / A10 exact-SHA review REQUIRED. **Never mark W002 VERIFIED from this document alone.**

## Locked provenance

- v2.0 original archival ZIP SHA256 `de0f36f5c270e07abedc0e317d8517c8f5e0d86a5a42897120d42b3a1f5dec47` (verified in isolated A0 workspace).
- Original PLAN v1.1 archival ZIP SHA256 `154f632edc3d6352c6d57a95b7c1b6e8d038aa8782a34d1d571538c791a87ffe` (verified).
- GitHub immutable mirror: `reference/spec_v2_machine_readable/plc_signal_registry.csv`, `reference/spec_v2_machine_readable/field_verification_gates.csv` and `docs/plan/v1.1-original/FIELD_GATES_14.csv`.
- 45 signal rows, 14 field gates (7 BLOCKED + 7 OPEN), zero field signoffs and zero allowed PLC production writes. Static crosswalk copies selected original columns byte-for-value; never promotes `ActivationAllowed`.

## Decision / uncertainty register (deliberately unresolved)

| Source signal | Current static fact | Forbidden inference | Safety decision | Field signoff needed |
| --- | --- | --- | --- | --- |
| D1030/D1032/D1034, D1000/D1002/D1004 | Historical XYU outputs and PLC-side references differ, D1000 alarm alias exists | These are equivalent active coordinates or word ordering | HOLD, SIM_ONLY | FG-01 |
| D1013 | Rear line-scan and clear alias share D1013 | A shared edge is safe or clears exactly one workpiece | HOLD, do not route PLC IO | FG-02 |
| D2000..D2019 | Old reading says 20 WORD; old PLC may write only 10 WORD | Barcode encoding, length or NUL behavior settled | Preserve raw evidence, identity uncertain | FG-03 |
| D2100..D2119 | End barcode source not site verified | Equal to infeed ID, trusted part identity | HOLD on uncertain identity | FG-04 |
| D1016, D2099 | Logical final quality and downstream request differ | NG physical reject hardware behavior certified | No production NG routing claim | FG-05 |
| Dual-host product switch | Historic master/slave coordination unknown | Atomic release activation/rollback already works | DRAFT only, no machine activation | FG-06 |
| D1015 / camera settings | Turntable/camera trigger and UserSet details unverified | One trigger ACK means all captures OK | Do not treat CamDone as final OK | FG-07 |
| Remaining FG-08..14 | OPEN unsigned gates | OPEN is a production permission | FIELD_PENDING | FG-08..14 |

## Reproducible isolated A0 evidence

Command A (actual on local read-only reference mirror):
`python3 docs/reviews/verify_w002.py --repo-root . --negative` -> **exit 0**, 45/45 signal equality, 14/14 gates, 6/6 mutation negatives rejected.

Command B (fresh second pass):
`python3 docs/reviews/verify_w002.py --repo-root .` -> **exit 0**, same static source invariants. Both raw outputs committed under `docs/reviews/evidence/W002_*.log`.

Evidence log SHA256 (first): `9f1807cc4853c5f13a67d2bf6ca1d5d27eaefda06354db1cf23ee8c448d91c0b`; verifier SHA256 `f75dcf2764a3ceee0a74a974f5e89a0e1d3887aaa59f728ea94c195f5a8d6525`.

All tests are static source+policy checks, NOT physical FINS reads, PLC protocol functional tests, or 14 FIELD gate acceptance. A10 must re-run from the exact PR head, inspect original locked sources, supply independent PASS/HOLD and SHA before A0 considers W002 ledger VERIFIED. Agent02 may begin W006 only after that separate ledger decision. No `main`, source archives, hardware configuration or production write flags were changed in this PR.