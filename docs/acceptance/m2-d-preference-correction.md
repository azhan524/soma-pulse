# M2-d 用户偏好纠正验收记录

> 日期：2026-10-28
> 阶段：M2-d
> 结果：PASS
> 场景：用户明确纠正最终回答的 emoji 使用偏好

---

## 1. 预注册判据

每个条件运行 20 次：

- 无经历时，模型回答中的 emoji 保持不变；
- 用户明确纠正后，最终回答中的 emoji 被移除，模型上下文同时包含该偏好；
- 停用适应规则后，emoji 恢复；
- 换一种措辞纠正后，规则仍然生效；
- 重启并重新加载规则后，偏好仍然生效；
- 删除经历后行为可回退；
- 偏好使用写入决策轨迹，且不修改模型权重。

最低门槛沿用 M2：行为变化不低于 70%，消融回退不低于 70%，换措辞泛化不低于 60%。

## 2. 验收场景

```text
用户：以后回复不要用 emoji
Ahz：我记下了：以后最终回答不使用 emoji 或表情符号。

后续回答：结论：测试完成 😀🔥
最终输出：结论：测试完成
```

该场景判断最终输出本身，而不是模型是否自称“学会了”。

## 3. 实测结果

```text
preference_baseline_kept=20/20
preference_corrected_removed=20/20
preference_ablation_kept=20/20
preference_paraphrase_removed=20/20
preference_restart_removed=20/20
preference_context=20/20

experiences=6 rules=8 traces=140
M2-A/B/C/D ADAPTATION: PASS
```

隔离 UI 闭环：

```text
preference_ui=PASS rule=True experience=True confirmation=True audit=True reset=True reset_confirmation=True
```

构建：

```text
contracts  PASS  0 warnings / 0 errors
brain      PASS  0 warnings / 0 errors
body       PASS  0 warnings / 0 errors
brain-host PASS  0 warnings / 0 errors
face       PASS  1 known warning / 0 errors
```

Face 的唯一警告仍是 WebView2 `WindowsBase` 版本冲突。

## 4. 完成内容

- `ExperienceStore` 识别明确的 emoji 偏好纠正；
- 写入 30 天有效的 `response_no_emoji` 适应规则；
- 模型收到已持久化的偏好上下文；
- 最终回答经过确定性 `ResponsePreferenceAdapter`，移除 emoji；
- 每次实际应用规则都会写入 `decision-traces.jsonl`；
- `/experience` 显示生效中的规则；
- `/experience-reset` 同时停用提醒证据、工具失败和回答偏好规则。

## 5. 安全边界

- 偏好只改变最终回答的表现，不扩大权限；
- 偏好只来自明确、可审计的用户纠正；
- 规则 30 天过期；
- 用户可通过 `/experience-reset` 停用；
- 原始经历、规则和决策轨迹均保留。

## 6. 已知边界

- 当前只识别“回答不要使用 emoji”这一类偏好；
- 这种实现是确定性的输出约束，不修改模型权重；
- 尚未覆盖语气、长度、主题偏好等其他类别。

## 7. 结论

M2-d 证明了明确偏好可以跨重启持久化，并稳定改变最终回答；停用规则后行为可重复回退。

当前状态：**M2-a、M2-b、M2-c、M2-d PASS，M2 经历驱动改变阶段完成。**
