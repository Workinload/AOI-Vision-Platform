# 历史 ZIP 交接协议：仅灾备用途，不再是默认开发主流程

本文件曾用于 M00-r001 的无 Git 协作。`docs/adr/ADR-0005-GITHUB-FIRST.md` 已将默认团队交付方式改为**GitHub Git branch + Pull Request + 独立 QA**。不得按旧版“所有 Agent 必须从 r001 ZIP 创建补丁、再交给 A0 手工集成”指令开展新的正式任务。

只有当 GitHub 临时不可用、且 **A0 书面批准**并锁定精确源码 SHA 和任务依赖时，才允许使用 `tools/create_patch.py`、`tools/validate_patch.py`、`tools/integrate_patch.py` 作为灾备封存工具。该协议仍有以下约束：只允许修改个人所有权路径；不跳过 `DependsOn`、WBS 正式 ledger、A10 独立复查；不允许外部现场生产写；ZIP manifest 必须可复算。

注意：`tools/create_patch.py` 只认识五个历史聊天身份，不支持直接生成 A0 所有权补丁；其工具支持范围和 `tests/patch/` 所有权约束属于历史局限，**不得**通过冒名他人解锁。GitHub 日常 PR 不依赖 ZIP 版本号作为任务门禁。

历史原文保存在完整正式 `M00-r003 OFFICIAL_SOURCE.zip` 中，SHA256：`737a140ff2da49da1428edd8152060da1818e0c3981711c105beecff6239f468`。
