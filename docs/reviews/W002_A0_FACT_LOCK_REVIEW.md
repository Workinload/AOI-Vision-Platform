# W002 — A0 locked legacy-facts review (Issue #6)

**DRAFT_FOR_A10_REVIEW — W002 NOT VERIFIED.** Source v2 ZIP SHA256 `de0f36f5c270e07abedc0e317d8517c8f5e0d86a5a42897120d42b3a1f5dec47`; original PLAN ZIP SHA256 `154f632edc3d6352c6d57a95b7c1b6e8d038aa8782a34d1d571538c791a87ffe`.

This change contains an exact 45-signal selected-field crosswalk and 14 field gates from GitHub's immutable `reference/spec_v2_machine_readable/` source. It does not invent commissioned addresses or enable hardware.

| Legacy ambiguity | Unresolved disposition |
| --- | --- |
| D1030/32/34 vs D1000/02/04 XYU, D1000 alarm aliases | HOLD until present PLC/version and unit readback, FG-01 |
| D1013 line-scan completion/clear alias | HOLD until PLC handshake edge evidence, FG-02 |
| D2000 window 20 WORD vs reported 10 WORD PLC transfer | Preserve raw words, barcode length/encoding UNKNOWN, FG-03 |
| D2100 final barcode source | Identity UNKNOWN, FG-04 |
| D1016 quality versus D2099 downstream request | No physical NG routing claim, FG-05 |
| Asymmetric dual-host changeover | No release activation, FG-06 |
| Camera trigger/UserSet readback and FG08–14 | Remain unsigned and FIELD_PENDING |

All 45 `ProductionWriteEnabled=FALSE`; all 14 `ProductionWriteAllowed=FALSE`; 7 `BLOCKED` and 7 `OPEN`, none signed. Simulator code and official `main` runtime remain unchanged.

To independently replay on the exact PR head:
```bash
python3 docs/reviews/verify_w002.py . --negative
python3 docs/reviews/verify_w002.py .
```
A0 earlier isolated local verification against byte-locked v2/PLAN ZIPs returned exit 0, 45/45 and 14/14 exact matches with six deliberate mutants rejected; this newly assembled GitHub HEAD script requires **A10 independent exact-SHA replay** and is not represented as having been run in GitHub Actions. W002 remains pending A10 evidence and explicit A0 ledger decision, no change to `docs/agents/TASK_LEDGER.json`.
