# M00 audit round 1 · Static source, safety and ownership

Reviewer: A0 self-review, **not independent A10**. Result: 12/12 static structural assertions PASS (source-contract script + manual inspection).

1. Exact original v2 SHA256 and CRC checked.
2. Embedded selected v2 machine reference file SHA256 verified.
3. 114 original RQ and 114 TC count/unicity verified.
4. 21 modules owner registry known.
5. 45 PLC signal entries all production write false.
6. 14 site gates 7 BLOCKED/7 OPEN all production write false.
7. 9 physical capture devices / 11 result channels verified from original CSV.
8. WBS 130 IDs in PLAN v1.1 and no task status tampered.
9. M00 source has only SIM_ONLY Core and UDP HEALTH vPLC; no physical driver source.
10. UI commands deny modification; no arbitrary PLC address writable.
11. Agent file ownership and ZIP task-owner checks exist.
12. C00-DRAFT not falsely released as a completed contract; SIM_READY remains false.

Open blockers: A10 sign-off, W007 Windows SDK ABI, W004 actual C01 freeze, actual FINS/cameras, all future SIM acceptance.
