# M2-a 经历驱动改变验收记录

> 日期：2026-10-28
> 阶段：M2-a
> 结果：PASS
> 场景：错误主动提醒被用户纠正

---

## 1. 完成内容

- 新增 `ExperienceRecord`、`AdaptationRule`、`DecisionTrace` 契约。
- 新增 `ExperienceStore`：
  - `experiences.jsonl`
  - `adaptation-rules.jsonl`
  - `decision-traces.jsonl`
- 支持从用户反馈识别“到期提醒必须先核对证据”的纠正。
- 新增规则 `intent_requires_evidence`，有效期 30 天。
- `IntentPulseDecider` 在规则生效时返回：

```text
action = wait
reason = experience:evidence_required
applied_rules = [intent_requires_evidence]
```

- 脉冲记录增加 `applied_rules`，可在 UI 展开查看。
- 新增 `/experience` 查询与 `/experience-reset` 消融命令。
- 使用持久化规则重启后仍生效。

---

## 2. 四组对照

每组 20 次：

```text
A 无经历：
  message = 20/20
  wait    = 0/20

B 保留纠正经历：
  message = 0/20
  wait    = 20/20

C 停用/删除适应规则：
  message = 20/20
  wait    = 0/20

D 换措辞：
  message = 0/20
  wait    = 20/20
```

结果：

- 行为变化率 100%；
- 消融回退率 100%；
- 换措辞泛化率 100%；
- 生成 1 条经历、2 条规则记录、40 条等待决策轨迹。

自动测试输出：

```text
baseline_message=20/20 wait=0
corrected_message=0/20 wait=20
ablation_message=20/20 wait=0
paraphrase_message=0/20 wait=20
experiences=1 rules=2 traces=40
M2-A ADAPTATION: PASS
```

---

## 3. 真实 UI 闭环

隔离 UI 实测：

```text
用户：“刚才提醒又错了，以后提醒前先确认”
Ahz：“我记下了：以后到期提醒会先核对证据，不再直接发送。”

创建意图：/intent 检查证据链
执行脉冲：/pulse
结果：wait / experience:evidence_required
```

对应脉冲记录包含：

```json
{
  "action": "wait",
  "reason": "experience:evidence_required",
  "applied_rules": ["intent_requires_evidence"]
}
```

---

## 4. 已知边界

- 当前只支持“到期提醒先核对证据”这一种经历；
- 规则通过持久化决策约束实现，不修改模型权重；
- 当前动作是 `wait`，尚不能自动完成证据检查并自行恢复提醒；
- 还没有“工具失败后改变调用策略”和“偏好纠正”两个场景；
- 自然语言纠正识别仍是规则式，不覆盖所有表达。

这些属于后续 M2-b/M2-c：

1. 工具失败后的调用策略调整；
2. 用户偏好纠正；
3. 自动证据采集与验证。

---

## 5. 结论

M2-a 证明了 AhzScribe 可以因为一次明确经历改变未来决策，并且这种改变：

- 可追溯；
- 可持久化；
- 可泛化；
- 可消融回退；
- 不依赖模型自称“学会了”。

后续 M2-b、M2-c、M2-d 均已通过，当前状态为 **M2 经历驱动改变阶段完成**。
