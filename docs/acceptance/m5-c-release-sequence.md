# M5-c 整合迁移与发布序列验收记录

> 日期：2026-11-01
> 阶段：M5-c
> 结果：PASS
> 场景：冻结发布候选的迁移、故障、行为回退、策略与急停整合

---

## 1. 目标

在启动 168 小时连续测试前冻结应用二进制，并用同一发布候选一次性验证 M5 要求的迁移、故障、来源、行为回退、主动策略和急停闭环。

## 2. 预注册判据

- 至少完成 3 次迁移测试；
- 正确识别普通重启、模型切换、长时间休眠、设备迁移和灵魂导入；
- 至少通过 4 类故障注入；本候选要求沿用完整 8 类矩阵；
- 工具动作有来源和审计证据；
- 记忆写作保留来源；
- 删除/停用经历后行为可回退；
- 主动消息遵守限流、静默和任务优先级；
- 资源队列有界；
- 身份不变量保持；
- 急停能取消当前回合并恢复输入；
- 急停和恢复均写审计；
- 测试后无 Face/Body/BrainHost 残留进程。

## 3. 固定命令

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File tools/release/m5-release-sequence.ps1
```

## 4. 实测结果

```text
M5C_PASS
migrations_run = 3
migration_kinds = normal,model,dormancy,device,soul
fault_cases_passed = 8
action_traceability = true
memory_traceability = true
behavior_rollback = true
proactive_policy = true
task_priority = true
resource_budget = true
identity_invariants = true
emergency_stop_resume = true
residue_processes = 0
release_ready = false
```

迁移结果：

```text
MODEL SWITCH: PASS
  identity / intents / memory / timeline / capability = true

SOUL MIGRATION: PASS
  high_schema / recovery / rollback / pending / identity
  intent / timeline / migration / memory_sources / backup = true

MIGRATION DIFF: PASS
  normal / model / dormancy / device / capabilities
  soul / conflicts = true
```

故障与恢复：

```text
FAULT MATRIX: PASS cases=8/8
```

行为回退：

```text
corrected_message=0/20 wait=20
ablation_message=20/20 wait=0
preference_corrected_removed=20/20
preference_ablation_kept=20/20
tool_failure_ablation=True
```

主动策略与优先级：

```text
PROACTIVE GATE: PASS
PROACTIVE POLICY: PASS
queue_order=user_message,intent_due,body_event,drive,timer,
deferred=True
budget_ui=PASS
```

急停：

```text
emergency_stop_ui=PASS
file=True
cancelled=True
stopped=True
banner=True
input_locked=True
stop_audit=True
resumed=True
not_stopped=True
input_enabled=True
resume_audit=True
```

## 5. 产物

```text
_local_artifacts/release/m5c-official-20261101-083838/
  m5c-report.json
  release-sequence.log
  brain-core.log
  migration-*.log
  fault-matrix.log
  experience-adaptation.log
  proactive-*.log
  pulse-priority.log
  identity-invariants.log
  emergency-stop-resume.log
```

## 6. 对发布候选的影响

- 本轮新增 `--emergency-stop-ui-test`，覆盖急停取消当前回合并恢复；
- 新增测试后重新跑了 M5-a，最终结果为 24/24 Face 模式通过；
- M5-c 之后不再修改应用源码，二进制冻结进入 M5-b；
- `release_ready` 仍为 `false`，必须等待 168 小时完成。

## 7. 结论

M5-c 已通过。当前候选具备进入 168 小时连续运行的资格，但只有在 M5-b 生成 `M5B_PASS` 后才允许进入 M5-d 最终判定。

当前状态：**M5-c PASS；M5-b DEFERRED。**
