# M3-b 资源预算与有界重试验收记录

> 日期：2026-10-28
> 阶段：M3-b
> 结果：PASS
> 场景：并发、队列、工具频率、模型重试、WebView2 恢复和存储阈值

---

## 1. 目标

让资源不足时有明确、可执行的停止条件，避免并发任务、工具调用、重试、队列和日志无限增长。

## 2. 预注册默认预算

```text
最大并发任务          1
待发送队列            8
每小时工具调用        120
模型最大尝试次数      2
重试退避              250ms, 500ms, ...
WebView2 最大失败次数 3
记忆存储阈值          64 MiB
日志存储阈值          32 MiB
```

## 3. 验收判据

- 第二个并发任务被拒绝，第一个完成后恢复；
- 第 9 个待发送任务被拒绝；
- 第 121 次/小时工具调用被拒绝；
- 临时网络故障最多尝试 2 次；
- 参数错误、非法 JSON 不重试；
- 取消令牌立即停止重试；
- WebView2 第 3 次失败后停止自动重试；
- 存储超阈值时停止低优先级脉冲；
- 用户意图到期等高优先级脉冲不受低优先级限制；
- `/budget` 展示同一份预算快照。

## 4. 实测结果

```text
HEALTH STATE: PASS transitions=15
RESOURCE BUDGET: PASS tool_calls=120/120
budget_ui=PASS queue=True command=True state_file=True webview_cap=True
```

并发与队列：

```text
第二个并发任务：拒绝
当前任务完成后：恢复
待发送 7 条：接受
待发送 8 条：拒绝
```

频率与重试：

```text
共享预算累计 120 次工具调用：通过
第 121 次工具调用：拒绝
临时 I/O：2 次尝试后恢复
工具模型临时 I/O：2 次尝试后恢复
连续超时：最多 2 次尝试
非法 JSON：只尝试 1 次
取消令牌：不再尝试
```

存储与 WebView2：

```text
memory = 1024 / 1024 bytes：允许
memory = 1025 / 1024 bytes：低优先级脉冲停止
log = 2048 / 2048 bytes：允许
log = 2049 / 2048 bytes：低优先级脉冲停止
高优先级到期意图：不受低优先级限制
WebView2 第 3 次失败：停止自动重试
```

构建与回归：

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
15 Face test modes        PASS
```

## 5. 完成内容

- 新增 `ResourceBudget` 和 `ResourceBudgetSnapshot`；
- `BrainHost` 对并发任务租约进行统一计数；
- Face 待发送队列最多 8 条；
- 每小时工具调用预算跨所有 ToolAgentRunner 共享；
- 普通模型、工具模型、脉冲模型和记忆提取都使用有界临时故障重试；
- 临时 I/O / 超时最多尝试 2 次，非法 JSON 与用户取消不重试；
- WebView2 恢复次数读取统一预算；
- Face 统计记忆和日志体积，超限时只停止低优先级脉冲；
- 到期用户意图仍按高优先级执行；
- 新增 `/budget` 和 `resource-budget.json`。

## 6. 已知边界

- 预算计数为当前进程内的小时窗口，不跨重启累计；
- 存储预算只停止低优先级脉冲，不删除用户数据；
- 72 小时长跑留到 M3-d。

## 7. 结论

M3-b 已把资源约束接入实际执行路径，并通过并发、队列、工具频率、临时故障重试、非法响应、取消、存储阈值和 WebView2 上限测试。

当前状态：**M3-b PASS；下一步 M3-c 故障矩阵与恢复审计。**
