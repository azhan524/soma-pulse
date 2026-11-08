# M2-c 工具失败后的调用策略验收记录

> 日期：2026-10-28
> 阶段：M2-c
> 结果：PASS
> 场景：同一工具、同一参数重复失败

---

## 1. 完成内容

- `AdaptationRule` 增加：
  - `Subject`：工具名；
  - `TriggerKey`：工具名与规范化参数的 SHA-256。
- `ExperienceStore` 新增：
  - `RecordToolFailure`
  - `ToolRetryBlockReason`
  - `ResetToolRules`
- 工具失败后自动记录经历和 7 天有效的 `block_identical_retry` 规则。
- `ToolAgentRunner` 在调用 body 前检查规则：
  - 相同工具 + 相同参数：直接返回经验阻止原因；
  - 参数变化：不被该规则阻止；
  - 规则停用或过期：恢复调用。
- `/experience-reset` 现在同时停用意图证据规则和工具失败规则。

---

## 2. 对照测试

每个对照 20 次：

```text
首次相同调用：
  body 实际执行 = 20/20

规则生效后的相同调用：
  body 实际执行 = 0/20
  规则阻止并且模型收到 ERROR 原因

停用规则后：
  body 实际执行 = 20/20
```

测试输出：

```text
tool_failure_rule=20/20
tool_failure_ablation=True
M2-A/B/C ADAPTATION: PASS
```

---

## 3. 安全边界

- 规则只阻止完全相同的工具和参数组合；
- 不扩大工具权限；
- 不绕过审批；
- 失败记录保留原工具审计 ID；
- 规则有 7 天过期时间；
- 用户可通过 `/experience-reset` 清除适应。

---

## 4. 已知边界

- 当前只会阻止重复相同调用，尚不会自动选择替代工具；
- 嵌套参数对象的规范化仍使用简单字符串转换；
- 失败原因没有按异常类型细分；
- 用户偏好纠正尚未实现。

---

## 5. 结论

M2-c 证明了 AhzScribe 不会在相同失败上无限重试，并且改变可以持久化、可消融、可追踪。

当前状态：**M2-c PASS；M2-d 用户偏好纠正随后通过，M2 阶段完成。**
