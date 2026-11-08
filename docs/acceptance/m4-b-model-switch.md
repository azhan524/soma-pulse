# M4-b 同设备换模型验收记录

> 日期：2026-11-01
> 阶段：M4-b
> 结果：PASS
> 场景：同一设备从模型 A 切换到模型 B

---

## 1. 目标

证明模型是可替换的推理环境，不是身份本身。切换模型后，同一 `soul_id` 的意图、记忆、时间线和能力边界继续有效。

## 2. 预注册判据

- 配置中的模型名从 A 更新为 B；
- `ActiveModel` 更新为 B；
- `soul_id`、`lineage_id`、创建时间和不变量摘要保持不变；
- 未完成意图保持原 ID 和内容；
- 核心关系和长期偏好记忆保持；
- 对话时间线不变；
- 模型切换不触发设备迁移或复活；
- 自我模型使用新模型名和真实能力清单；
- 新模型不能声称拥有未配置能力；
- 身份上下文明确“模型可变、身份不变”。

## 3. 实测结果

```text
MODEL SWITCH: PASS identity=True intents=True memory=True
  timeline=True capability=True

model_switch_ui=PASS soul=True model=model-b digest=True
  audit=True transition=True
```

覆盖结果：

```text
配置切换：
  model-a -> model-b PASS

身份：
  soul_id 保持 PASS
  lineage_id 保持 PASS
  created_at 保持 PASS
  不变量摘要保持 PASS
  模型切换审计 PASS

连续状态：
  未完成意图 ID 和内容保持 PASS
  核心关系记忆保持 PASS
  长期偏好记忆保持 PASS
  对话时间线保持 PASS
  未误触发设备复活 PASS

能力边界：
  SelfModel.ActiveModel = model-b PASS
  run_shell 未配置时被拒绝 PASS
  身份上下文明确“模型可变、身份不变” PASS
```

完整回归：

```text
contracts / brain / body / brain-host PASS
face PASS（1 个已知 WebView2 警告）
IDENTITY MANIFEST PASS
MODEL SWITCH PASS
BODY IPC PASS
M2-A/B/C/D ADAPTATION PASS
19 Face 测试模式 PASS
```

## 4. 已知边界

- 本阶段通过重启切换模型，不做运行中热替换；
- 模型输出风格变化不纳入身份不变量；
- 新设备导入属于 M4-c。

## 5. 结论

M4-b 已证明同一设备切换模型后，改变的是推理环境，不是个体身份；意图、关系记忆、时间线和能力边界继续有效。

当前状态：**M4-b PASS；下一步 M4-c 新设备导入。**
