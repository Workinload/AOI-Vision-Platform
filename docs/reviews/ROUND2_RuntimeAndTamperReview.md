# M00 audit round 2 · Build, runtime and negative behavior

Reviewer: A0 runtime checks, **not independent A10**.

- Real Linux Qt6.11.2 QML `aoi-ui` compiled+loaded: PASS.
- Real Linux OpenCV5.0.0 linked/cvtColor: PASS.
- C++ domain draft safety invariants tests: PASS.
- Two independent Core roles with independent SQLite WAL and local IPC: PASS.
- Two UI processes get real IPC status from corresponding Core: PASS.
- One independent vPLC loopback UDP HEALTH endpoint: PASS. FINS: NOT_IMPLEMENTED.
- Five distinct active PIDs and PNG screenshot captures, images decoded after complete write: PASS.
- Negative: PLC write-shaped IPC rejected, nonhealth UDP ignored: PASS.
- Negative: SIM_ONLY OFF compile blocked; bad role, DB collision, remote bind refused: PASS.
- ZIP protocol: exact SHA, ownership/WBS identity/prerequisite protection, no candidate self-approval: PASS.
- Clean ZIP first run found a test-fixture-only ignore-pattern bug deleting `docs/evidence/`; fixed by limiting excluded `evidence` to repository root. Final clean rebuild/rerun result recorded in external `M00_FINAL_VERIFY.txt`.

No real 114/52 test execution; do not change test statuses.
