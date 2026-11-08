# M4-a 身份清单与不变量验收记录

> 日期：2026-11-01
> 阶段：M4-a
> 结果：PASS
> 场景：稳定身份、设备/模型切换、权限变化与非法改写

---

## 1. 目标

把分散的 `soul_id`、设备来源、模型、权限和核心边界收口为可验证的 `IdentityManifest`，证明环境变化不等于身份变化。

## 2. 预注册判据

- 首次启动生成身份清单和独立不变量摘要；
- 普通重启后 `soul_id`、`lineage_id`、创建时间和全部不变量保持不变；
- 同一设备切换模型时只更新 `ActiveModel`；
- 新设备启动时只更新 `CurrentDeviceId`；
- 权限档变化不能突破 `PermissionCeiling`；
- 直接修改不变量会被摘要校验拒绝；
- 错误 `soul_id` 会被拒绝；
- 身份损坏时进入安全模式；
- 普通长时间休眠不能触发“换了身体”的复活语义；
- 身份清单和摘要进入灵魂备份。

## 3. 实测结果

```text
IDENTITY MANIFEST: PASS invariants=6
identity_ui=PASS manifest=True digest=True valid=True invariants=6
  command=True dormancy=True device_change=True
```

覆盖结果：

```text
首次创建：
  identity-manifest.json PASS
  identity-invariants.sha256 PASS
  6 个不变量 PASS

同设备重启：
  soul_id 保持 PASS
  lineage_id 保持 PASS
  created_at 保持 PASS

换模型：
  仅 ActiveModel 变化 PASS
  不变量摘要不变 PASS

换设备：
  仅 CurrentDeviceId 变化 PASS
  soul_id 和 lineage_id 保持 PASS

权限变化：
  权限档可降低 PASS
  不能突破 PermissionCeiling PASS

非法改写：
  soul_id 不匹配被拒绝 PASS
  不变量篡改被摘要校验拒绝 PASS

时间语义：
  同设备长时间休眠不触发 resurrection PASS
  设备变更触发 resurrection PASS

灵魂备份：
  identity-manifest.json 往返 PASS
  identity-invariants.sha256 往返 PASS
```

完整回归：

```text
contracts / brain / body / brain-host PASS
face PASS（1 个已知 WebView2 警告）
IDENTITY MANIFEST PASS
BODY IPC PASS
M2-A/B/C/D ADAPTATION PASS
17 Face 测试模式 PASS
```

额外修复：

- `IntentPulseDecider` 的 24 小时证据有效期改为使用同一个可注入时钟；
- 修复“新鲜证据/过期证据”结果随实际运行时刻反转的问题。

## 4. 已知边界

- M4-a 只固定身份不变量，不执行完整跨设备迁移；
- 模型切换和灵魂包导入的完整流程属于后续 M4-b/M4-c；
- 核心关系、长期承诺和自传摘要将在 M4 后续阶段加入清单。

## 5. 结论

M4-a 已证明设备、模型和权限档属于可变环境，而 `soul_id`、血缘、创建时间和核心边界属于不可静默改写的身份不变量。

当前状态：**M4-a PASS；下一步 M4-b 同设备换模型。**
