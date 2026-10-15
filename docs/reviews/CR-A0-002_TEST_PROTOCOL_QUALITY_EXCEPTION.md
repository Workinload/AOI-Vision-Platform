# CR-A0-002 — Candidate-only unowned test protocol remediation

**Authority:** A0 source integration coordinator. **Scope:** M00-r003 candidate only.
**Approval decision:** ALLOW_A0_EXCEPTION_IN_CANDIDATE / INDEPENDENT_A10_SIGNOFF_REQUIRED.
**Previous official source:** M00-r001 (unchanged).
**Held M00-r002 candidate SHA256:** `159fb58d25782007d5439f50e2df256ff253c979257ef679ac3154ca41884022`.
**A10 r002 independent QA decision:** HOLD / unmodified CTest 5/6 FAIL / screenshot false positive.
**Standard A10 patch:** `AOI_PATCH_AOI-Agent05_base_M00-r002_CANDIDATE_W005_W007.zip`, SHA256 `23ee6d77aa07451197dd6c963d30f4e1acb574a229f73a4fe1426796b081a2dc`.
**Standard integration tool:** `tools/integrate_patch.py`, exact r002 candidate baseline SHA, output new r003 unverified candidate (SHA256 `7967a6d707191f73bd85b4d15cb56464aa06a2c2344ea14a38fc407be44db5db`).

## Explicit one-file exceptional action

The existing `tests/patch/test_zip_protocol.py` has no assigned role in `docs/agents/OWNERSHIP.json`, so an A10 patch for this path must fail ownership checks. A10 filed the independent **test-only CR proposal**. A0 authorizes a **one-time A0-controlled remediation overlay** to the *unreleased r003 candidate* of that **one path**, using the **exact text diff proposed by A10**, without forging an `AOI-Agent05` patch identity. Under this exception A0 does not represent the change as an A10-owned patch or a completed WBS.

The change derives the next candidate version from `RELEASE.json` rather than pinning `M00-r002`; all positive and negative ZIP-protocol cases are preserved. The full diff hash, `patch` console output, and subsequent fresh tests must be stored outside the source as evidence. Do not edit `docs/agents/OWNERSHIP.json` retroactively to legitimize this exception.

## WBS, source, field and signoff boundary

A10's **legitimately owned** source changes are integrated by the original strict validator/integrator as a *candidate-only* patch (not as an automatic W005 or W007 final acceptance). A0 has independently inspected both affected files, confirmed their SHA256 equals the A10-supplied payload, and found no user SDK, PLC address, field write gate, immutable `reference/` or business logic changes. W001 provenance retains the A0-approved `VERIFIED/SOURCE_PROVENANCE_ONLY` **within the candidate ledger**, no other WBS status is advanced.

**No promotion is authorized by this CR.** A10 must independently rebuild and re-run the exact **final candidate ZIP**, including all six unmodified CTests, injected missing-screenshot negative case, ZIP protocol attack rejections, SDK/manifest integrity and SIM_ONLY prohibition. A0 may promote only after A10 issues explicit PASS for the final SHA. 114 business TC / 52 scenarios / Windows native / 14 field signoffs remain unverified. Current immutable M00-r001 official source remains untouched.
