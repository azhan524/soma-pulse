# M3-c 故障矩阵与恢复审计验收记录

> 日期：2026-10-28
> 阶段：M3-c
> 结果：PASS
> 场景：网络、模型、WebView2、body、审计、记忆和工具连续失败

---

## 1. 目标

逐项注入 M3 故障矩阵中的故障，验证系统能识别故障、限制受影响能力、恢复，并留下开始与恢复审计。

## 2. 预注册故障

```text
network_disconnect
model_timeout
model_invalid_json
webview_crash
body_pipe_stop
audit_broker_unavailable
mmy_corrupt_block
continuous_tool_failures
```

## 3. 判据

- 每类故障至少注入一次；
- 故障期间能力按健康状态限制；
- 恢复后状态回到正确基线；
- 每类故障有 `started` 和 `recovered` 审计；
- `.mmy` 坏块不进入正常记忆，其他有效块保留；
- 连续工具失败在工具循环上限停止；
- 模型非法 JSON 不重试，也不伪装成功；
- 测试只使用临时目录和测试 body 进程。

## 4. 实测结果

```text
network_disconnect=PASS
model_timeout=PASS
model_invalid_json=PASS
webview_crash=PASS
body_pipe_stop=PASS
audit_broker_unavailable=PASS
mmy_corrupt_block=PASS
continuous_tool_failures=PASS
FAULT MATRIX: PASS cases=8/8
```

恢复审计：

```text
每类故障 started 记录       8/8
每类故障 recovered 记录     8/8
故障审计对                  16/16
```

关键结果：

```text
网络异常：第一次失败，第二次恢复，健康状态保持可用
模型超时：有界重试后恢复
非法 JSON：拒绝且不重试
WebView2 故障：Degraded，只读工具仍可用，恢复后 Healthy
body 故障：真实停止子进程，工具被拒绝；重启后 Healthy
审计不可用：ReadOnly，只读工具允许，写工具拒绝；解锁后恢复
.mmy 坏块：坏块隔离到 quarantine.jsonl，2 个有效块全部保留
连续工具失败：4 次后按工具循环上限停止
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
BRAIN CORE / HEALTH       PASS
BRAIN CORE / BUDGET       PASS
M2 ADAPTATION             PASS
16 Face test modes        PASS
```

新增实现：

- `MmyMemory.ParseDetailed` 返回有效块、坏块和读取错误；
- 正常解析只返回有效块，坏块不再进入记忆；
- `MemoryStore` 将坏块写入 `memory/quarantine.jsonl` 并写审计；
- `ResourceBudget` 发出模型重试开始/恢复事件，Face 写入审计；
- 故障矩阵入口：`AhzScribe.Face.exe --fault-matrix-test`。

## 5. 已知边界

- 网络和模型超时在模型适配器边界注入；
- WebView2 崩溃通过真实状态回调共用的健康路径注入；
- 审计 broker 不可用通过主审计文件独占锁注入；
- body 使用真实隔离子进程完成停止和重启；
- 72 小时连续运行仍属于 M3-d。

## 6. 结论

M3-c 的八类故障全部通过，且每类故障都有开始和恢复审计。坏 `.mmy` 块现在会被隔离，不再混入正常记忆。

当前状态：**M3-c PASS；下一步 M3-d 72 小时稳态测试。**
