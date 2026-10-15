# ZIP-first agent delivery protocol (no Git remote)

**All five subchats start from one single immutable `AOI_Vision_Platform_M00_r001_SOURCE.zip`.** One ZIP cannot be regarded as shared writable storage. A new base can only be issued by A0 after A10 review.

1. A0 provides current source ZIP and a role-specific chat prompt in `docs/agents/CHAT_START_*.md`. Each agent unzips under its private workspace (never source `/AOI开发项目` or `/工具开发`).
2. Before starting a WBS task, read the `reference/plan_v1.1/WBS_TASKS_130.csv` row and check *all* prerequisites in A0's verified task ledger. If unavailable, only perform planning/review, do not falsely mark as implemented.
3. An agent changes **owned paths only** (`OWNERSHIP.json`). CMake root belongs to A10; shared contracts/typed IDs to A0; cross-module changes require a CR approval first.
4. Make patch with `python3 tools/create_patch.py --base /path/M00-r001_SOURCE.zip --worktree /path/unpacked/worktree --agent AOI-Agent02 --tasks W023,W024 --out /path/AOI_PATCH_Agent02_base_M00-r001.zip`.
5. `create_patch.py` validates base ZIP hashes, file ownership, exact base archive SHA256, and computes changed-file SHA256. The patch ZIP only includes `patch.json` plus source payload under `payload/`. No absolute paths, symlinks, `..`, executables, full SDKs, or build outputs.
6. **Actual integration is blocked automatically** if any task DependsOn has not been marked `VERIFIED` in `docs/agents/TASK_LEDGER.json` of the exact base ZIP. Patch creation can prepare a blocked task but cannot bypass the integration gate. Upload that patch ZIP in current A0 main chat. A0 first checks `python3 tools/validate_patch.py --base current-source.zip --patch agent-patch.zip`, reviews source/tests; never copy agent files over current worktree by hand.
7. If valid, `python3 tools/integrate_patch.py --base current-source.zip --patch agent-patch.zip --out AOI_Vision_Platform_M00_r002_CANDIDATE.zip --release-id M00-r002` creates an immutable **candidate** copy. A10 independently builds/runs tests; A0 approves promotion to official release and archives old known-good ZIP.
8. If base archive SHA256 differs, STOP. Rebase/recreate patch on the current integrated ZIP; do not force apply. Tasks from different agents can only share current base until one is integrated, at which point later patches based on old archive need refresh/rebase.
9. Never merge patch claims blindly: tests can be falsified, and any modification to write policy, product identity, model/result statuses, interface boundaries or schema must receive A0 + A10 review.
10. Deliver final report with `AgentId, TaskIds, BaselineSHA256, changed files, commands, exit codes, evidence and gaps`.

**Legacy and site evidence remain read-only.** `SIM_READY` means real system tests W001..W122, not M00 smoke success. Preserve the 14 field gates as `FIELD_PENDING`.
