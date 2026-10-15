# ADR-0001 — ChatGPT chats as logical agents, ZIP-only delivery

Date: 2026-10-15. Status: Accepted for M00 by user (Git remote postponed). Use one canonical source archive plus checked patches. A0 controls integration; A10 has independent testing authority. No implicit cross-chat filesystem sharing, no uncontrolled overwrite, no edits to two resource libraries.

Reason: User explicitly prefers all work within one ChatGPT Project and accepts manual transfer overhead. This favors reliable handoff/rollback over Git remote automation. If Git is enabled in the future, source release ZIPs and patch manifests can be imported with recorded version history; no Git workflow is a prerequisite today.
