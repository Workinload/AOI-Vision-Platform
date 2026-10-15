# A10 exact-head independent QA signoff — 2026-10-16
PR #12 `39598da19fd43eecc839a6c13047a2b78b58f366`: **PASS_ROUTING_ONLY**, two GitHub Actions rounds exit 0, 6/6 negatives each.
PR #27 `d13235a96db67f586ac972183473a698ef309e23`: **HOLD_BAD_CSV**, two rounds exit 1, `KeyError: SignalId`.
PR #28 `d7ace91ad1fc1fab0739d93280262315f9651944`: **HOLD_BAD_CSV**, two rounds exit 1, `KeyError: ModuleId` in both candidate validators.
PR #29 `1dc75f37b28b76e1f028799521897f1ab686d9f7`: **PASS_DRAFT_ONLY / HOLD_C01_FREEZE**, two rounds exit 0, upstream eight tests PASS twice, independent 7/7 negative mutations each, but JSON schema missing inter-field ERROR+OK condition and W003 not VERIFIED.

Full independent Actions logs: https://github.com/Workinload/AOI-Vision-Platform/actions/runs/38768294200 . Each round verifies 55 original PLAN hashes, 130 WBS, 114 RQ/TC, 14 field production writes FALSE; no hardware execution.

CI #21 real run https://github.com/Workinload/AOI-Vision-Platform/actions/runs/38740220207 : static provenance SUCCESS, missing SDK build FAILURE, full CTest not executed. CI NOT VERIFIED.
W005 PR #22 real two-round test https://github.com/Workinload/AOI-Vision-Platform/actions/runs/38768539548 : two exit 0 / five mutation negatives per round; 114 TC & 52 scenarios NOT EXECUTED, W005 NOT VERIFIED.
Windows SDK nine volumes reconstructed SHA: Qt `d44d42b8f3172c02fefff8442d84dbdc5f60b933f7953b0231bbaae2ea113db8`; OpenCV `b9c7a30fcbed462fd11885a2e55d475bdad03c3b8be970473290b6e52f2a7bef`. Windows native GUI, plugin closure and clean-run evidence missing, W007 HOLD.

Offline report & local real SDK SHA log: `/AOI开发成果/A10_UPSTREAM_AUDIT_20261016.md`, `A10_INDEPENDENT_QA_EVIDENCE_20261016.zip`. No main merges or WBS promotions. Agent05-authored verifier and workflow require independent A0 review.