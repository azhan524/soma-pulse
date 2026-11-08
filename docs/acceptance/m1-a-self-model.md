# M1-a 功能自我模型与来源验收记录

> 日期：2026-10-28
> 阶段：M1-a
> 结果：PASS
> 范围：契约、能力清单、来源、自我模型、设置页与自检

---

## 1. 完成内容

- 新增 `SourceKind`、`SourceRef`、`Provenance`；
- 新增 `CapabilityDescriptor`、`SelfModelSnapshot`；
- 新增共享 `ToolPolicyCatalog`，brain 和 body 使用同一份工具权限表；
- body 通过命名管道提供真实能力清单；
- `ActionResult` 携带工具来源和审计 ID；
- `MemoryItem` 携带来源、置信度和推断标记；
- `.mmy` 与 JSONL 都能保存和恢复记忆来源；
- `PulseDecision` 和 `PulseEvent` 携带来源；
- brain 生成自我模型，并把能力与限制注入模型上下文；
- face 在设置页显示 `soul_id`、权限档和能力数量；
- `/selfcheck` 显示自我模型和来源；
- 工具执行结果会写入 `RecentActions`，可用于行动归属判断；
- 自我模型在启动、设置保存后刷新。

---

## 2. 安全行为

- body 提供 42 项真实工具能力；
- 其中 19 项需要审批；
- 只读模式下 17 项可用，写入和 Full 能力被标记为不可用；
- `rollback_self` 现在要求 `Full` 且 `Approved=true`；
- 用户文本或模型输出不会改变身体能力；
- 未出现在 `RecentActions` 中的审计 ID 不会被认作自己的行动；
- 工具失败时，来源记录使用实际的 blocked 审计。

---

## 3. 验证结果

### 构建

```text
contracts  PASS  0 warnings / 0 errors
brain      PASS  0 warnings / 0 errors
body       PASS  0 warnings / 0 errors
brain-host PASS  0 warnings / 0 errors
face       PASS  1 known warning / 0 errors
```

Face 的已知警告为 WebView2 的 `WindowsBase` 版本冲突，本轮未处理。

### 自动测试

```text
BODY SELF-TEST            PASS
CAPABILITIES              42
APPROVAL CAPABILITIES     19
PROVENANCE                PASS
BODY IPC                  PASS
BRAIN CLIENT SMOKE        PASS
TOOL LOOP BODY            PASS
ACTION OWNERSHIP          PASS
SELF MODEL                PASS
CANCEL PROPAGATION        PASS
S2 MEMORY                 PASS
MMY DUAL WRITE            PASS
BRAIN MEMORY/PULSE        PASS
```

### Face 回归

```text
S3 BODY                   PASS
PROACTIVE GATE            PASS
PROACTIVE POLICY          PASS
DRIVE ENGINE              PASS
PULSE QUEUE               PASS
S0 STATE                  PASS
FACE SESSION EPISODE      PASS
SOUL ROUNDTRIP            PASS
SCHEMA GUARD              PASS
SENSOR STALE RESTART      PASS
```

### 端到端 UI

隔离启动 body 与 face 后验证：

```text
self_model.json 生成          PASS
soul_id 持久化                PASS
只读模式能力过滤              PASS
42 项能力 / 17 项可用         PASS
写入和 Shell 被标记不可用      PASS
设置页显示自我模型             PASS
/selfcheck 显示自我模型来源    PASS
Face 六 Tab 保持响应           PASS
```

---

## 4. 尚未覆盖

- M1-b：用户/网页/文档内容与自我事实冲突时的完整响应链；
- 跨进程模型上下文中的长期自我模型更新；
- RecentActions 的跨重启持久化展示；
- 能力清单的平台可用性探测；
- 多显示器、非 Windows 环境和干净机器验证。

---

## 5. 结论

M1-a 提供了第一版结构化、可查询、可注入模型上下文的自我模型。

它还不能完成完整的来源冲突推理，但已经具备后续 M1-b、M2 和迁移验证需要的基础。
