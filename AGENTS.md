# AOI Multi-agent guardrails — M00-r001

- Read `README.md`, `BASELINE_LOCK.json`, `RELEASE.json`, `docs/agents/START_HERE.md`, `docs/agents/OWNERSHIP.json`, `contracts/C00_DRAFT.md`, `reference/plan_v1.1/WBS_TASKS_130.csv` before coding.
- This project is SIM_ONLY. The binaries do not include real PLC/camera adapter. Never connect hardware or loosen PLC write permissions.
- Source-of-truth v2.0 and `/工具开发` remain read-only. Original spec contains 114 RQ, 114 TC, 21 modules, 14 site verification gates. All site production-write flags false.
- Cross-chat development is manual ZIP transfer; no assumed shared filesystem and no Git remote requirement. Every developer gets exact integrated base ZIP, task ID, allowed path, and must return a validated patch ZIP with base archive SHA256.
- Modification of `contracts/`, `libs/domain/`, WBS, reference source, or release/ownership metadata requires explicit A0 approval. A10 must not sign off its own unreviewed changes.
- `C00-DRAFT` is not the final v1 interface, and M00 smoke passing is not SIM_READY. No OK on missing frames, ambiguous identity, unverified barcode or processing fault.
- Never record a task `SIM_PASS` without actual test evidence and independent verification. `FIELD_PENDING` persists until site evidence and signoff.
- Mandatory: named file ownership, versioned patch, tests, changed file list, source hashes, explicit NOT_VERIFIED limitations.
