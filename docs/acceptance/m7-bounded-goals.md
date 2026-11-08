# M7 有界目标形成验收记录

> 日期：2026-11-01
> 阶段：M7
> 结果：PASS
> 场景：来源、状态机、限流、脉冲、用户关闭与防复活

---

## 1. 目标

让目标来自未完成承诺、冲突、证据缺口、长期关注或用户明确意图，而不是模型随机生成。目标是可暂停、等待、完成、取消和过期的有界状态，不是无限自主任务。

## 2. 预注册判据

- 目标状态包含 Candidate、Monitoring、Due、Paused、Waiting、Done、Cancelled、Expired；
- 每个目标必须有来源和证据；
- 模型提议可以成为 Candidate，但不能晋升；
- 每日新增目标有上限；
- 同时活跃目标有上限；
- 活动任务永远优先；
- 用户连续不回应后目标自动暂停；
- 到期目标能进入脉冲提醒；
- `none` 和 `wait` 都是正常结果；
- 用户可关闭、暂停、恢复、等待和完成目标；
- 已取消目标不能由相同来源指纹自动复活；
- 目标进入模型上下文和灵魂备份。

## 3. 实现

新增契约：

```text
GoalStatus
GoalSourceKind
GoalState
GoalDecision
```

新增：

```text
ahz-scribe-brain/src/GoalStore.cs
goals.jsonl
```

状态机：

```text
Candidate
  → Monitoring
  → Due
  → Paused ↔ Monitoring
  → Waiting
  → Done / Cancelled / Expired
```

来源：

```text
UnfinishedCommitment
MemoryConflict
EvidenceGap
AttentionChange
UserIntent
MigrationPending
LocalRule
ModelProposal（仅 Candidate，不可晋升）
```

默认限制：

```text
每日新增目标 = 3
同时活跃目标 = 3
目标有效期 = 7 天
连续不回应暂停阈值 = 2
```

限制可由环境变量覆盖：

```text
AHZ_GOAL_DAILY_MAX
AHZ_GOAL_ACTIVE_MAX
AHZ_GOAL_VALID_DAYS
AHZ_GOAL_NO_RESPONSE_PAUSE
```

## 4. 实测结果

Brain：

```text
BOUNDED GOALS: PASS
source=True
model_guard=True
limits=True
task_priority=True
due_wait_none=True
no_response_pause=True
close_no_revive=True
expiry=True
```

覆盖结果：

```text
来源：
  用户意图 CANDIDATE -> MONITORING PASS
  无证据提议拒绝 PASS
  模型提议只能 Candidate PASS
  模型提议晋升被拒绝 PASS

限制：
  同时活跃目标超限拒绝 PASS
  每日新增目标超限拒绝 PASS

优先级：
  活动任务存在时返回 none / active_task PASS

状态：
  Due PASS
  连续两次不回应进入 Paused PASS
  Waiting PASS
  用户取消 Cancelled PASS
  有效期结束 Expired PASS

正常结果：
  none / no_goal PASS
  wait / goal_waiting PASS

防复活：
  相同来源指纹的已取消目标无法重新 propose PASS

重启：
  goals.jsonl 恢复关闭状态 PASS

脉冲：
  到期目标生成 bounded_goal:due 消息 PASS
  CorrelationId 指向 goal_id PASS
  消息发出后目标进入 Waiting PASS
```

UI：

```text
goal_ui=PASS
created=True
listed=True
task_priority=True
wait_resume_pause=True
cancelled=True
no_revive=True
context=True
pulse=True
```

灵魂备份：

```text
soul_roundtrip=True
autobiography=True
attention=True
lifecycle=True
usage=True
goals=True
backup=True
pending=True
unsafe_zip_blocked=True
```

新增命令：

```text
/goals
/goal add <目标>
/goal pause <id>
/goal resume <id>
/goal wait <id>
/goal done <id>
/goal cancel <id>
/goal drive
```

完整回归：

```text
contracts / brain / body / brain-host：0 errors
face：0 errors，仅 1 个已知 WebView2 警告
BRAIN MEMORY/PULSE：PASS
AUTOBIOGRAPHY：PASS
ATTENTION：PASS
MEMORY LIFECYCLE：PASS
BOUNDED GOALS：PASS
28 个 Face 测试模式：全部 PASS
```

## 5. 已知边界

- 当前自动候选主要来自本地记忆冲突、证据缺口和长期关注；
- `MigrationPending` 来源已建模，但迁移目标的具体入口留在整合阶段；
- 目标不会直接执行工具，只通过提醒和模型上下文影响后续行动；
- 用户连续不回应按目标提醒计算，不替代现有主动消息总限流。

## 6. 结论

M7 已证明目标形成是有来源、有上限、有状态、有结束条件的行为；模型随口提议不能越级成为长期目标，活动任务和用户控制始终优先。

当前状态：**M7 PASS；下一步 M8 安全自我改造闭环。**
