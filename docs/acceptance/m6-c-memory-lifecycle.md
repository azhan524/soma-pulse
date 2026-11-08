# M6-c 记忆巩固、遗忘与恢复验收记录

> 日期：2026-11-01
> 阶段：M6-c
> 结果：PASS
> 场景：归档、遗忘、降级、加固、冲突保留和恢复

---

## 1. 目标

让记忆可以巩固和遗忘，但不允许静默物理删除。低价值旧情节进入可恢复归档，无直接证据的模型解释降级，重要关系和冲突保持原强度。

## 2. 预注册判据

- 高频、近期和重要记忆获得更高检索分数；
- 一年以上、低价值、非重要的情节记忆进入 `archived`；
- 关系、承诺、身份、安全和权限相关记忆不自动归档；
- 模型推断且无直接证据的记忆进入 `downgraded`；
- disputed 冲突记忆保留双方来源，不自动归档或降级；
- 用户遗忘为 `forgotten` 软状态，原记录不物理删除；
- forgotten 记忆不进入检索结果；
- 恢复后重新回到遗忘前的有效状态；
- 归档、遗忘、恢复、降级、加固均追加生命周期事件；
- 生命周期操作和巩固写入审计；
- 重启后状态和生命周期保持；
- 生命周期、使用统计进入灵魂备份。

## 3. 实现

新增契约：

```text
MemoryLifecycleAction
MemoryLifecycleEvent
MemoryConsolidationResult
```

新增文件：

```text
memory-lifecycle.jsonl   追加式生命周期事件
memory-usage.json        有界的检索使用计数
```

生命周期状态：

```text
active
  → archived / forgotten / downgraded
  → restored（回到原状态）
```

检索评分：

```text
关键词匹配
+ 近期衰减
+ 重要性加成
+ log2(1 + 使用次数)
- archived 惩罚
- downgraded 惩罚
- inferred 惩罚
```

`forgotten` 和 `deleted` 不进入检索。

## 4. 实测结果

Brain：

```text
MEMORY LIFECYCLE: PASS
archived=True
important_kept=True
downgraded=True
conflict_preserved=True
reinforced=True
forget_restore=True
audit=True
restart=True
```

覆盖结果：

```text
归档：
  400 天前低价值情节 → archived PASS
  400 天前核心关系 → active PASS

降级：
  模型推断且无直接证据 → downgraded PASS

冲突：
  disputed 记忆保留 PASS
  十一月/十二月两条值和 User/Tool 来源均保留 PASS

使用频率：
  同一记忆检索 3 次 → Reinforce 事件 PASS

软遗忘：
  forgotten 状态写入 PASS
  原 JSONL/MMY 记录仍存在 PASS
  forgotten 不出现在检索结果 PASS

恢复：
  restore 后回到 active PASS
  恢复后重新参与检索 PASS

审计：
  memory_forget PASS
  memory_restore PASS
  memory_consolidate PASS

重启：
  archived / downgraded 状态保持 PASS
  lifecycle 事件保持 PASS
```

UI：

```text
memory_lifecycle_ui=PASS
forget=True
restore=True
consolidate=True
command=True
events=3
```

灵魂备份：

```text
soul_roundtrip=True
autobiography=True
attention=True
lifecycle=True
usage=True
backup=True
pending=True
unsafe_zip_blocked=True
```

新增命令：

```text
/memory-forget <id>
/memory-restore <id>
/memory-archive <id>
/memory-consolidate [天数]
/memory-lifecycle
```

完整回归：

```text
contracts / brain / body / brain-host：0 errors
face：0 errors，仅 1 个已知 WebView2 警告
BRAIN MEMORY/PULSE：PASS
AUTOBIOGRAPHY：PASS
ATTENTION：PASS
MEMORY LIFECYCLE：PASS
27 个 Face 测试模式：全部 PASS
```

## 5. 已知边界

- 巩固是逻辑状态覆盖，不压缩或删除原始 JSONL/MMY；
- 使用频率只按实际检索结果计数，不把记忆被展示视为使用；
- 自动归档只处理旧情节，普通语义记忆不会自动归档；
- 遗忘不提供不可恢复的物理擦除；若未来需要合规擦除，必须新增独立审批流程；
- 归档阈值仍由命令或后续维护流程触发，不是后台周期任务。

## 6. 结论

M6-c 已证明记忆系统能区分重要与低价值、证据与推断、遗忘与物理删除；任何影响检索状态的变化都有生命周期事件和审计，并且可以恢复。

当前状态：**M6-c PASS；M6 自传式记忆与长期关注阶段完成。**
