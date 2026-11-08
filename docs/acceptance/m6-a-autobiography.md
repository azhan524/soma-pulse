# M6-a 自传式摘要与证据链验收记录

> 日期：2026-11-01
> 阶段：M6-a
> 结果：PASS
> 场景：事实、解释、猜测、冲突分层及跨模型上下文

---

## 1. 目标

把可检索记忆升级为可追溯的时间叙事。每条自传条目必须能指回原始记忆或状态证据；模型推断不得冒充事实，冲突不得被静默选择。

## 2. 预注册判据

- 自传支持日、周、主题三种周期；
- 支持事实、解释、猜测、冲突四种层；
- 所有层都必须携带证据；
- 事实层不能只有模型推断作为来源；
- 叙事条目不能作为另一条叙事的证据；
- 冲突条目至少保留两个独立证据；
- 不存在的证据不能通过验证；
- 重启后自传条目和证据链保持；
- 重复生成相同内容不重复追加；
- 换模型后仍能通过审计和记忆看到“最近在关注什么”；
- 一次无关焦点只能进入猜测层，不能写入事实或身份；
- 自传进入灵魂备份。

## 3. 实现

新增：

```text
AutobiographyPeriod
AutobiographyLayer
AutobiographyEvidence
AutobiographyEntry
AutobiographyVerification
AutobiographyBuildResult
ahz-scribe-brain/src/AutobiographyStore.cs
```

存储文件：

```text
autobiography.jsonl
```

条目规则：

- `Fact`：必须有不来自模型的原始证据；
- `Interpretation`：可以引用记忆，但文案明确标注为解释；
- `Guess`：只能指向当前焦点等状态线索，并明确不是事实；
- `Conflict`：同键出现不同值时保留全部证据；
- 所有 `EvidenceId` 禁止使用 `narrative:` 前缀；
- 生成 ID 由周期、层、正文、证据共同决定，重复生成不会重复落盘。

## 4. 实测结果

Brain：

```text
AUTOBIOGRAPHY: PASS
entries=8
facts=3
interpretations=3
conflicts=1
restart=True
evidence_guard=True
```

覆盖结果：

```text
事实层：
  memory:mem_auto_1 ... mem_auto_4 PASS
  正文可追到原记忆 PASS

解释层：
  只引用原始记忆 PASS
  模型推断记忆进入解释层 PASS

猜测层：
  当前焦点只进入 Guess PASS
  不写入 Fact / Interpretation / 身份 PASS

冲突：
  project:deadline 的十一月/十二月同时保留 PASS
  双方 evidence id 同时存在 PASS

拒绝项：
  无证据事实被拒绝 PASS
  memory:does-not-exist 被验证拒绝 PASS
  narrative: 循环证据被拒绝（实现规则） PASS

持久化：
  重启后条目数与证据链保持 PASS
  重复生成不重复追加 PASS

换模型上下文：
  两个独立模型对象都收到“可追溯自传上下文” PASS
  上下文都包含证据数量 PASS
```

UI：

```text
autobiography_ui=PASS
command=True
context=True
persisted=True
entries=7
```

灵魂备份：

```text
soul_roundtrip=True
autobiography=True
backup=True
pending=True
unsafe_zip_blocked=True
```

完整回归：

```text
contracts / brain / body / brain-host：0 errors
face：0 errors，仅 1 个已知 WebView2 警告
BRAIN MEMORY/PULSE：PASS
AUTOBIOGRAPHY：PASS
25 个 Face 测试模式：全部 PASS
```

新增 UI 命令：

```text
/autobiography
```

该命令刷新最近七天的日/周/主题摘要，并展示每层前四条证据 ID。

## 5. 已知边界

- 当前摘要由确定性规则从记忆和焦点生成，不调用模型写叙事；
- 日/周/主题已具备，但“按自然周归档旧条目”的巩固策略属于 M6-c；
- 长期关注对象的状态机与目标/提醒影响属于 M6-b；
- 当前只支持证据 ID 校验，不校验原始文件被外部删除后的物理可读性。

## 6. 结论

M6-a 已证明：自传可以生成，但事实不能凭空产生，解释不能冒充事实，猜测不能改写身份，冲突不会静默丢来源，换模型后仍能读取同一段可追溯经历。

当前状态：**M6-a PASS；下一步 M6-b 长期关注。**
