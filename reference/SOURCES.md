# Reference inputs / read-only snapshot

Original evidence packages remain untouched in `/AOI开发项目` and `/工具开发`. This engineering ZIP only embeds machine-readable copies of selected v2 spec files and plan v1.1 snapshots (see `BASELINE_LOCK.json`). It does NOT bundle the full 31MB original v2 spec or SDK.

Agent must check original v2 zip SHA256 in lock if using full original evidence. A single ChatGPT conversation does not automatically acquire files created in other conversations.

All imported CSV/JSON under `reference/` are read-only; modifications to these copies require A0 source review and must never be mistaken for edits to the actual source-of-truth.
