# M6-b 长期关注状态机验收记录

> 日期：2026-11-01
> 阶段：M6-b
> 结果：PASS
> 场景：关注形成、主导转移、暂停、恢复、结束和过期

---

## 1. 目标

让“长期关注”不只是关键词，而是一个有来源、有证据、有有效期、能影响检索、能暂停和结束的持久状态。

## 2. 预注册判据

- 至少两条非模型证据才能自动形成长期关注；
- 单条模型推断不能形成长期关注；
- 关注包含来源、证据 ID、形成原因、最近证据、继续条件、结束条件和失效时间；
- 多条关注按证据数量、来源数和近期性评分；
- 高评分关注成为当前主导关注；
- 新证据可以造成主导关注自然转移；
- 关注能改变记忆检索排序；
- 关注上下文进入文本和工具模型；
- 换模型后同一关注仍可读取；
- 暂停后不再主导，恢复后重新参与；
- 用户结束后不能因旧记忆自动复活；
- 有效期结束进入 Expired；
- 重启后状态保持；
- 关注进入灵魂备份。

## 3. 实现

新增：

```text
AttentionStatus
AttentionState
ahz-scribe-brain/src/AttentionStore.cs
```

存储：

```text
attention.jsonl
```

状态机：

```text
Candidate / Monitoring
  → Paused ↔ Monitoring
  → Ended
  → Expired
```

关注评分：

```text
证据数量
+ 独立来源数量 × 1.5
+ 最近 30 天时间奖励
```

默认有效期为 14 天。关注只影响检索排序和模型上下文，不授予权限、不新增事实。

## 4. 实测结果

Brain：

```text
ATTENTION: PASS
formed=True
dominant_transfer=True
retrieval=True
pause_resume_end=True
restart=True
model_guard=True
expiry=True
```

覆盖结果：

```text
自然形成：
  project 由 2 条非模型证据形成 PASS
  来源数和证据数正确 PASS

主导转移：
  archive 3 条证据形成后成为 dominant PASS

检索影响：
  活跃关注相关记忆被加权 PASS

暂停/恢复/结束：
  Paused 后由 project 主导 PASS
  Monitoring 恢复后 archive 重新主导 PASS
  Ended 后旧记忆刷新不会复活 PASS

模型边界：
  model-only 证据不能形成关注 PASS
  无证据不能形成关注 PASS

过期：
  20 天后未刷新关注进入 Expired PASS
  已 Ended 状态不会被过期流程改写 PASS

重启：
  attention.jsonl 可恢复关注和证据 ID PASS

换模型：
  两个独立模型对象均收到长期关注上下文 PASS
```

UI：

```text
attention_ui=PASS
listed=True
paused=True
resumed=True
ended=True
dominant_none=True
persisted=True
```

灵魂备份：

```text
soul_roundtrip=True
autobiography=True
attention=True
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
ATTENTION：PASS
26 个 Face 测试模式：全部 PASS
```

新增 UI 命令：

```text
/attention
/attention pause <主题>
/attention resume <主题>
/attention end <主题>
```

## 5. 已知边界

- 当前主导关注由评分即时决定，没有单独的人工“锁定主导”状态；
- 自动形成仍基于记忆标签/键前缀的重复证据，不会自行理解任意自然语言主题；
- 关注对目标是输入信号，真正把关注转成有界目标属于 M7；
- 失效时间到期由读取或刷新路径触发，不是后台独立定时器。

## 6. 结论

M6-b 已证明长期关注可以自然形成、持续、转移、暂停、恢复、结束和过期；它会影响检索与模型上下文，但不会增加事实或权限，也不会因为旧记忆而自动复活。

当前状态：**M6-b PASS；下一步 M6-c 巩固、遗忘与恢复。**
