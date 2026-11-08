# M4-c 新设备导入验收记录

> 日期：2026-11-01
> 阶段：M4-c
> 结果：PASS
> 场景：源设备导出灵魂包，新设备导入与恢复

---

## 1. 目标

证明跨设备导入后，逻辑个体保持同一身份，同时保留来源不同的记忆，并能在导入中断时安全回滚后重试。

## 2. 预注册判据

- 高版本灵魂包在写入目标文件前被拒绝；
- 导入中断会恢复导入前状态；
- 中断后可重新导入；
- `migration_pending` 记录源设备、目标设备和源 soul；
- 导入后 `soul_id`、血缘和身份不变量保持；
- `CurrentDeviceId` 更新为目标设备；
- 未完成意图和源设备时间线恢复；
- 源设备关系和目标设备已有记忆可以同时存在，并保留各自来源；
- 迁移完成后触发一次跨设备接续；
- 导入前目标状态备份存在。

## 3. 实测结果

```text
SOUL MIGRATION: PASS
high_schema=True
recovery=True
rollback=True
pending=True
identity=True
intent=True
timeline=True
migration=True
memory_sources=True
backup=True
```

覆盖结果：

```text
高版本备份：
  schema=2 在写入前拒绝 PASS
  目标身份和记忆不变 PASS

导入中断：
  第二次文件复制后注入失败 PASS
  自动恢复到导入前状态 PASS
  新增半成品文件被删除 PASS
  目标身份、时间线保持 PASS

崩溃恢复：
  migration_journal.json 存在时启动恢复 PASS
  中断临时文件删除 PASS
  current_state 恢复 PASS

重试：
  回滚后重新导入成功 PASS

迁移结果：
  source_device_id 保留 PASS
  target_device_id 记录 PASS
  source_soul_id 保留 PASS
  原目标状态备份存在 PASS

身份与连续状态：
  soul_id / lineage / 不变量摘要保持 PASS
  CurrentDeviceId 更新为新设备 PASS
  未完成意图恢复 PASS
  源设备时间线恢复 PASS
  设备迁移和灵魂导入被识别 PASS

混合来源记忆：
  源设备核心关系记忆保留 PASS
  目标设备原有情节记忆保留 PASS
  两边 OriginDevice / Provenance 均可区分 PASS
```

完整回归：

```text
contracts / brain / body / brain-host PASS
face PASS（1 个已知 WebView2 警告）
IDENTITY MANIFEST PASS
MODEL SWITCH PASS
SOUL MIGRATION PASS
BODY IPC PASS
M2-A/B/C/D ADAPTATION PASS
20 Face 测试模式 PASS
```

## 4. 已知边界

- 导入期间由 Face 停止脉冲并取消当前回合；
- 完整迁移观察期和旧设备并发运行冲突合并留在 M4-d；
- 版本过高的备份只支持拒绝，不做自动降级。

## 5. 结论

M4-c 已证明灵魂包可以在新设备恢复同一身份、意图、时间线和能力边界，同时保留旧设备与目标设备各自的记忆来源；导入中断可回滚并重试。

当前状态：**M4-c PASS；下一步 M4-d 迁移差异报告与观察期。**
