# M2-b 意图证据与等待恢复验收记录

> 日期：2026-10-28
> 阶段：M2-b
> 结果：PASS
> 场景：提醒等待证据 → 证据有效 → 恢复提醒

---

## 1. 完成内容

- 适应规则使到期提醒进入 `wait` 后，不再直接标记意图完成。
- 新增 `intent_evidence` 记忆记录，按意图 ID 关联。
- 证据有效期 24 小时。
- 有效证据存在时，脉冲决策恢复为 `message`：

```text
reason = experience:evidence_verified
applied_rules = [
  intent_requires_evidence,
  intent_evidence_verified
]
```

- `/intents`：列出未完成意图及 ID。
- `/intent-evidence [id或文本]`：记录一条与意图关联的证据。
- 证据过期或规则停用后，决策重新回到相应基线。

---

## 2. 对照测试

每组 20 次：

```text
无规则：
  message = 20/20
  wait    = 0/20

规则生效、无证据：
  message = 0/20
  wait    = 20/20

规则生效、有效证据：
  message = 20/20
  wait    = 0/20

规则生效、25 小时前证据：
  message = 0/20
  wait    = 20/20

停用规则：
  message = 20/20
  wait    = 0/20

换措辞规则：
  message = 0/20
  wait    = 20/20
```

测试输出：

```text
baseline_message=20/20 wait=0
corrected_message=0/20 wait=20
ablation_message=20/20 wait=0
paraphrase_message=0/20 wait=20
fresh_evidence_message=20/20 wait=0
expired_evidence_message=0/20 wait=20
experiences=3 rules=4 traces=80
M2-A/B ADAPTATION: PASS
```

---

## 3. 真实 UI 闭环

```text
以后提醒前先确认证据
→ /intent 检查证据链
→ /pulse
→ wait / experience:evidence_required

→ /intent-evidence 检查证据链
→ /pulse
→ message / experience:evidence_verified
```

两次脉冲都写入 `pulse.jsonl`，并带有：

```text
applied_rules = [intent_requires_evidence]
```

或：

```text
applied_rules = [intent_requires_evidence, intent_evidence_verified]
```

---

## 4. 已知边界

- 当前证据由用户显式记录，尚未自动检查文件、网页或工具状态；
- 证据只按意图 ID 关联，还没有证据类型和强度；
- 证据有效期目前固定为 24 小时；
- 工具失败后的调用策略调整仍未实现；
- 用户偏好纠正仍未实现。

---

## 5. 结论

M2-b 证明了经历规则不仅能阻止旧行为，还能在新证据出现时恢复执行，并保持完整的规则和来源轨迹。

当前状态：**M2-b PASS；M2-c、M2-d 随后通过，M2 经历驱动改变阶段完成。**
