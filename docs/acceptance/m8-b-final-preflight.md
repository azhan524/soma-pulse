# M8-b 最终候选预检记录

> 日期：2026-11-01
> 阶段：M8-b
> 结果：PASS
> 场景：M1–M8a 全矩阵、空目标恢复、安全自改与发布回归

---

## 1. 目标

在最终 168 小时运行前，确认当前候选已满足以下条件：

- M1–M7 持续通过；
- M8-a 安全自改闭环通过；
- 28 个 Face 模式通过；
- 3 次迁移与 5 类迁移识别通过；
- 8 类故障注入通过；
- 全部来源、审计、目标、急停和自改测试通过；
- 空目标灵魂导入与恢复通过；
- 无未关闭 P0/P1；
- 无测试进程残留。

## 2. 固定命令

```powershell
$env:AHZ_DOTNET = "<dotnet.exe>"
powershell -NoProfile -ExecutionPolicy Bypass `
  -File tools/release/m8-finalize.ps1
```

## 3. 实测结果

```text
M8B_PRECHECK_PASS
acceptance_docs = 23
clean_target_soul_restore = true
safe_self_evolution = true
body_self_test = true
body_ipc = true
release_verify = M5A_PASS
release_sequence = M5C_PASS
face_modes = 28/28
open_p0_p1 = 0
residue_processes = 0
pending_final_soak_hours = 168
release_ready = true
```

关键证据：

```text
AUTOBIOGRAPHY: PASS
ATTENTION: PASS
MEMORY LIFECYCLE: PASS
BOUNDED GOALS: PASS
SAFE SELF-EVOLUTION: PASS
MIGRATION DIFF: PASS
FAULT MATRIX: PASS cases=8/8
SOUL MIGRATION: PASS
BODY IPC: PASS
```

Body 能力：

```text
CAPABILITIES: 43 approval=20
PROVENANCE: PASS
```

## 4. 产物

正式预检：

```text
_local_artifacts/release/m8-preflight-20261101-091031/
  m8-preflight.json
  m8-preflight.log
  release-verify/
  release-sequence/
  body-self.log
  body-ipc.log
```

`m8-preflight.json` 的 `release_ready` 明确保持 `false`。

## 5. 最终 168 小时

已完成：

```text
result = M5B_PASS
duration_hours = 168.0079
samples = 10075
restart_ok = true
restart_health = Healthy
identity_preserved = true
max_pending_tasks = 0
max_logs_mb = 6.72
memory_growth_mb = -35.92
healthy_ratio = 1.0
max_sample_gap_seconds = 62.451
```

最终产物：

```text
_local_artifacts/soak/m5b-official-20261101-192431/summary.json
```

`AhzScribe-M5B-Soak` 计划任务和监控心跳已删除。

## 6. 当前状态

**M8-b PASS；M8 最终发布门槛完成。**

最终发布产物：

```text
_local_artifacts/release/m8-final-20261108-194016/
  m8-final-report.json
  source-snapshot.zip
```

允许声明：

> AhzScribe 是一个具有边界、自稳态、连续历史、受控适应和有限自主性的功能性硅基生命候选体。

该声明不表示已经证明主观意识、真实情感或无限自主行动。
