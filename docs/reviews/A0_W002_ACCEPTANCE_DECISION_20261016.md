# A0 W002 source-fact verification decision — 2026-10-16

**Decision: VERIFIED strictly for W002 original S00 legacy-static fact crosswalk. NOT field acceptance, NOT real PLC/IPC/Windows/W006 implementation.**

- Original W002 `DependsOn=W001`; W001 status VERIFIED source-provenance in ledger.
- Original W002 deliverable and acceptance: historical facts vs new design, ambiguity register, D1030/D1000 and barcode ambiguity must **not** be silently decided. A0 checked 45/45 signals and 14/14 FIELD gates; 7 BLOCKED + 7 OPEN, zero field signoffs, zero production write-enabled flags.
- A10 independent two pinned GitHub Actions rounds for exact W002 head `c0a3c66421e269a5ca22583cfa6e2fe1eb9b14b0`: both exit 0; six source mutation negatives rejected per round. Evidence: https://github.com/Workinload/AOI-Vision-Platform/actions/runs/38772323194 and /AOI开发成果/A10_NEW_SHA_REAUDIT_20261016.md.
- A0 manually checked changed-file set: only 7 `docs/reviews/` files; no original PLAN, reference, contract, RELEASE, ledger, A7, UI, PLC, device code or field-write policy changed in PR. A0 merged PR #27 in main commit `0d7abb19d319120ec083a6d909da7612b267f3ce`.
- Validated legacy unknowns still deferred to appropriate field gates: D1030/D1000 XYU meaning, D1013 alias, D2000 barcode window/encoding, D1016 vs D2099 physical reject routing, master/slave switch readback, remaining 14 FG. They are **not resolved by W002**.
- This ledger updates only W002 `status=VERIFIED` and preserves `scope=LEGACY_STATIC_FACTS_AND_UNCERTAINTY_REGISTER_ONLY`, all other WBS decisions unchanged. The original frozen `docs/plan/v1.1-original/WBS_TASKS_130.csv` is intentionally immutable and is **not** overwritten with work-progress statuses.
- Downstream W006/W008 are eligible to check *this static prerequisite* only; their own DependsOn, WritePolicy, hardware authorization and field gates remain independently enforced. No blanket enablement or direct hardware connection.
- A0 approval is strictly the documented W002 source fact gate; no new M00 SOURCE release and no assertion of SIM_READY or FIELD_ACCEPTED.
