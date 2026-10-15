# ADR-0005 — GitHub-first collaboration without weakening AOI acceptance

- **Date:** 2026-10-15
- **Status:** PROPOSED (A10 independent governance review required before merge)
- **Supersedes workflow only:** `ADR-0001-ZIP-only-team.md` (kept unchanged as historic evidence).
- **Official baseline:** `M00-r003 OFFICIAL_SOURCE`, SHA256 `737a140ff2da49da1428edd8152060da1818e0c3981711c105beecff6239f468`.
- **Git repo:** `Workinload/AOI-Vision-Platform` (PRIVATE).

## Decision

Use one canonical GitHub `main` as the **integration worktree**, topic branches for A0 + five worker chats, PR for each scoped change, and A10 independent test evidence before A0 merging or publishing. ZIP is relegated to reproducible release archives and exceptional offline fallback. Preserve v2.0 and PLAN v1.1 source ZIPs and their hash locks unchanged; publish a separate additive v1.2 execution overlay rather than rewriting the original plan/WBS.

Allow **parallel branch implementation/prototyping** on pending tasks, but forbid acceptance/merge while their official WBS dependencies are unverified. Explicitly separate CODE / MERGE / RELEASE / FIELD gate states.

## Why

ZIP-first M00 instructions still pin M00-r001 and prohibit agents from sharing Git branches, defeating the proven GitHub connector path. A7 implementation is unassigned, some paths lack ownership, C00 draft numbering conflicts with PLAN C-07..C-12, and private-repo rulesets are unavailable through current permissions/plan. A single GitHub account identity also does not establish independent A10 code review automatically.

## Consequences

A0 must update `AGENTS.md`, `README.md`, five chat starters, ownership and Git PR templates in a governance PR. A10 must separately implement CI gates before declaring automation complete. `main` uses latest Git commit SHA for code review; immutable release ZIP SHA labels only a formally promoted package, not every intermediate commit. Every change must remain SIM_ONLY and field gates locked.

## Rollback

Retain Git history and immutable M00-r003 ZIP; revert the governance PR if it causes unsafe behavior. Never reset or force-push shared `main` to imitate a release. Reinstating ZIP protocol requires a new A0 decision and independent QA.
