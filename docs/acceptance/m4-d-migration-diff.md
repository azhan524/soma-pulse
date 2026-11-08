# M4-d 迁移差异报告与观察期验收记录

> 日期：2026-11-01
> 阶段：M4-d
> 结果：PASS
> 场景：重启、休眠、换模型、换设备和灵魂导入

---

## 1. 目标

证明每次连续性事件都能留下可核对的前后差异，且迁移后的观察期可查询、可取消；旧设备与新设备产生同键记忆时保留冲突和双方来源。

## 2. 预注册判据

- 正确区分普通重启、长时间休眠、模型切换、设备迁移和灵魂导入；
- 每次启动生成 `migration-diff.json`，并保存当前 `migration-baseline.json`；
- 差异报告包含身份不变量、设备、模型、权限、意图、记忆来源、时间线缺口和能力变化；
- 迁移历史以追加方式写入 `migration-history.jsonl`；
- 非普通重启进入 24 小时观察期；
- 观察期可通过 `/migration` 查询，并可通过 `/migration cancel [原因]` 取消；
- 普通长时间休眠不会被描述成换设备；
- 灵魂包导入后接管源 `soul_id`，同时把当前设备更新为目标设备；
- 同键不同值记忆保留两条记录和各自来源，不静默覆盖；
- 同键同值但来源不同的记忆合并为一条逻辑记忆，并保留全部来源；
- 观察期取消写入审计。

## 3. 实测结果

```text
MIGRATION DIFF: PASS
normal=True model=True dormancy=True device=True
capabilities=True soul=True conflicts=True

migration_diff_ui=PASS
report=True history=True baseline=True query=True cancel=True
```

覆盖结果：

```text
普通重启：
  Kind = NormalRestart PASS
  建立基线，不进入观察期 PASS

模型切换：
  model-a -> model-b PASS
  身份摘要保持 PASS
  意图新增/状态变化被报告 PASS
  DeviceChanged = false PASS

长时间休眠：
  72 小时后识别为 LongDormancy PASS
  不触发 resurrection PASS
  时间线缺口写入报告 PASS

设备迁移：
  device-old -> device-new PASS
  身份不变量保持、设备变化被报告 PASS

能力变化：
  Full -> ReadOnly PASS
  write_file 等能力被列为 removed PASS

灵魂导入：
  源 soul_id 被接管 PASS
  当前设备更新为目标设备 PASS
  源设备记忆来源保留 PASS
  导入前目标状态备份存在 PASS

观察期：
  非普通迁移进入 Active PASS
  24 小时到期时间可查询 PASS
  错误 observation_id 无法取消 PASS
  正确取消后持久化为 Cancelled PASS

跨设备记忆：
  同键不同值保留两条及各自 OriginDevice PASS
  后到值标为 disputed PASS
  同键同值合并逻辑记录并保留双来源 PASS
```

## 4. 报告样例

实际运行文件：

```text
data/migration-diff.json
data/migration-baseline.json
data/migration-history.jsonl
data/migration-observation.json
```

`migration-diff.json` 的核心结构：

```json
{
  "report_id": "migration_20261101_083000_000_ab12cd",
  "kind": "model_switch",
  "baseline_initialized": false,
  "before": {
    "soul_id": "soul_example",
    "device_id": "device-a",
    "model": "model-a",
    "permission_profile": "interactive",
    "invariant_digest": "sha256-example",
    "intents": [],
    "memory_count": 12,
    "memory_sources": [
      { "device_id": "device-a", "count": 12 }
    ],
    "timeline": {
      "message_count": 40
    }
  },
  "after": {
    "soul_id": "soul_example",
    "device_id": "device-a",
    "model": "model-b",
    "permission_profile": "interactive",
    "invariant_digest": "sha256-example",
    "intents": [],
    "memory_count": 12,
    "memory_sources": [
      { "device_id": "device-a", "count": 12 }
    ],
    "timeline": {
      "message_count": 41
    }
  },
  "identity_preserved": true,
  "device_changed": false,
  "model_changed": true,
  "permission_changed": false,
  "capabilities_added": [],
  "capabilities_removed": [],
  "timeline_gap_seconds": 0,
  "timeline_messages_added": 1
}
```

## 5. 完整回归

```text
contracts / brain / body / brain-host：0 errors
face：0 errors，仅 1 个已知 WebView2 WindowsBase 警告

BRAIN MEMORY/PULSE：PASS
BODY SELF-TEST：PASS
BODY IPC：PASS
IDENTITY MANIFEST：PASS
MODEL SWITCH：PASS
SOUL MIGRATION：PASS
MIGRATION DIFF：PASS
FAULT MATRIX：PASS cases=8/8
M2-A/B/C/D ADAPTATION：PASS
23 个 Face 测试模式：全部 PASS
```

复跑命令：

```powershell
dotnet build ahz-scribe-face/src/AhzScribe.Face.csproj
ahz-scribe-face/src/bin/Debug/net10.0-windows/AhzScribe.Face.exe --migration-diff-test
ahz-scribe-face/src/bin/Debug/net10.0-windows/AhzScribe.Face.exe --migration-diff-ui-test
```

## 6. 已知边界

- 观察期默认 24 小时，当前只记录和查询状态，不替用户自动确认或回滚；
- `migration-diff.json` 保存最近一次启动差异，完整摘要追加到 `migration-history.jsonl`；
- 能力差异按 `ToolPolicyCatalog` 与当前权限档计算，不读取模型自述；
- 同键同值多来源会在逻辑读取时合并，磁盘仍保留追加式来源记录。

## 7. 结论

M4-d 已证明迁移不是一句“我还记得”，而是有前后快照、身份不变量校验、来源差异、时间线缺口、能力变化和可取消观察期的可复核过程。

当前状态：**M4 PASS；下一步 M5 整合与发布验证。**
