# M3-a 统一健康状态机验收记录

> 日期：2026-10-28
> 阶段：M3-a
> 结果：PASS
> 场景：model / body / memory / audit / webview 故障与恢复

---

## 1. 目标

把现有零散的 WebView2 失败、body 传感器重启和自检逻辑，收口为一个统一健康状态机，并让健康状态直接限制执行能力。

## 2. 预注册判据

- 所有组件正常时为 `Healthy`；
- body 不可用时为 `Degraded`，所有工具调用被拒绝；
- 模型不可用时为 `Degraded`，下一次普通对话仍可尝试恢复；
- 审计或记忆不可用时为 `ReadOnly`，只读工具仍可用，写工具被拒绝；
- WebView2 不可用时为 `Degraded`，但核心工具仍可执行；
- 急停时为 `Stopped`，禁止对话和工具；
- 组件恢复后自动回到正确状态；
- 每次状态转换有审计记录，并写入 `health.json`；
- `/health` 和 `/selfcheck` 展示同一份健康快照。

## 3. 实测结果

```text
HEALTH STATE: PASS transitions=15
health_ui=PASS degraded=True visible=True command=True audit=True state_file=True
health_fault=PASS body_pipe=True body_degraded=True webview_degraded=True recovered=True
health_audit_probe=PASS
```

完整回归：

```text
contracts  PASS  0 warnings / 0 errors
brain      PASS  0 warnings / 0 errors
body       PASS  0 warnings / 0 errors
brain-host PASS  0 warnings / 0 errors
face       PASS  1 known warning / 0 errors

BODY SELF-TEST            PASS  capabilities=42 approval=19
BODY IPC                  PASS
BRAIN CLIENT SMOKE        PASS
TOOL LOOP BODY            PASS
BRAIN CORE / HEALTH       PASS
M2 ADAPTATION             PASS
10 Face regressions       PASS
```

Face 唯一警告仍是既有的 WebView2 `WindowsBase` 版本冲突。

## 4. 完成内容

- `HealthState` / `HealthSnapshot` / `HealthTransition` 契约；
- `HealthManager` 汇总 model、body、memory、audit、webview；
- 状态优先级为 `Stopped > ReadOnly > Degraded > Healthy`；
- body 或 model 不健康时拒绝工具调用；
- audit 或 memory 不健康时允许只读工具、拒绝写工具；
- WebView2 故障只降级观察页，不阻断核心工具；
- 模型失败后下一次请求可重新验证并恢复；
- 新增 `/health`、`/health retry`，`/selfcheck` 共用同一快照；
- `health.json` 和审计记录保存状态与故障来源；
- 急停同步进入 `Stopped`，恢复后重新计算健康状态。

## 5. 故障注入

```text
body 管道缺失        PASS  Degraded，工具调用为 0
model 抛出 I/O 错误  PASS  Degraded，随后可恢复
memory 解析失败注入  PASS  ReadOnly，写动作被拒绝
audit 文件只读       PASS  Probe 返回失败，写动作策略为禁止
webview 崩溃注入     PASS  Degraded，只读工具仍可执行
急停与恢复           PASS  Stopped -> Healthy
```

## 6. 已知边界

- 本阶段不实现 72 小时长跑；
- 模型恢复通过下一次真实请求验证；
- `/health retry` 重新检查 body、memory 和 audit，模型在下一次对话时重新验证；
- 真实 `.mmy` 单块损坏与审计 fallback 隔离测试留到 M3-c；
- 资源预算、每小时工具上限和队列背压留到 M3-b。

## 7. 结论

M3-a 已把健康状态从展示信息变成执行约束，并通过 body 缺失、模型失败、审计只读、WebView 降级和急停恢复测试。

当前状态：**M3-a PASS；下一步 M3-b 资源预算与有界重试。**
