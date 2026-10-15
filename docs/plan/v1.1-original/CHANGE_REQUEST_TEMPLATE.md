# 公共合同 / 跨Agent代码变更申请模板

```
CCR-ID:
RequesterAgent:
AffectedTaskIds:
AffectedRequirementIds:
CurrentContractVersion:
ProposedContractVersion:
AffectedSchemaOrInterface:
ReasonAndSourceEvidence: [LEGACY_STATIC / INFERRED / PROPOSED / FIELD_REQUIRED]
ChangeDiff:
BackwardCompatibility: [ADDITIVE / BREAKING]
AffectedAgents:
AffectedSourcePaths:
StateOrDBMigration:
SimFixtureImpact:
SafetyImpact: [PLC WRITE / QUALITY / IDENTITY / DEPLOYMENT / UI / NONE]
NegativeTestsRequired:
RollbackPlan:
ProposedApprovalByA0:
IndependentQAByA10:
ExecutionEvidenceLinks:
```

未经 A0 批准与 A10 合同回归，不准让两个 Agent 自行修改同一公共 DTO；对 PLC、机器人、条码等 `FIELD_REQUIRED` 项不得通过 CCR 自动改为 `FIELD_VERIFIED`。
