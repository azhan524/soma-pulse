# M5-b 168 小时连续运行验收记录

> 开始日期：2026-11-01
> 阶段：M5-b
> 当前结果：PASS
> 场景：隔离 Face + Body 连续运行 168 小时

---

## 1. 目标

证明冻结后的 M5 发布候选可以连续运行 7 天，期间身份不变量、健康状态、队列、日志和内存都保持有界。

## 2. 预注册判据

- 实际持续时间不少于 167.9 小时；
- 每 60 秒采样一次；
- Face 与 Body 不退出；
- 每一次采样 `identity_preserved=true`；
- 采样间隔不超过 185 秒；
- 最大 pending tasks 不超过 8；
- 最大日志不超过 32 MiB；
- Working Set 增长不超过 512 MiB；
- Healthy 采样比例不低于 95%；
- 结束后用同一数据目录重启，Face/Body 存活且健康状态恢复为 Healthy；
- 生成 `summary.json` 且结果为 `M5B_PASS`。

任何 Windows 更新、系统重启、休眠或采样器中断造成连续运行断裂，本轮均判为失效。

## 3. 首次尝试（已按用户要求暂停）

```text
计划任务：AhzScribe-M5B-Soak
输出目录：_local_artifacts/soak/m5b-official-20261101-084134
开始时间：2026-11-01 08:41:37 Asia/Shanghai
预计结束：2026-11-08 08:41:37 Asia/Shanghai
时长：168 小时
采样间隔：60 秒
```

运行前系统检查：

```text
CBS RebootPending = false
Windows Update RebootRequired = false
Face/Body 测试进程残留 = 0
```

冻结二进制 SHA-256：

```text
F17A1A4A886F52C3762D6C4F7111CCAFCF552EFACAAB23DC93545C712B5576AD  AhzScribe.Face.exe
1266DBE639C6BEC4BFC244296B2C50B4EA2E320DEE18388880D334104BF86C17  AhzScribe.Body.exe
C83576E5A9D712E6D0DBBCA4AB3A9A028F8E11B747FB47C97862C73EE329A58D  AhzScribe.Brain.dll
E1F62ED4C0E6CDF0D5E3E5AB63010ACF32D35308FD6EC5F0430A7E29AE9D4187  AhzScribe.Contracts.dll
```

实际运行到 sample 4 / 3.039 分钟，用户要求暂缓 M5-b，因此本轮不计入 PASS/FAIL：

```text
result = DEFERRED
reason = user requested deferral
sample = 4
elapsed_minutes = 3.039
face_alive = true
body_alive = true
identity_preserved = true
health = Healthy
face_working_set_mb = 72.5
body_working_set_mb = 28.14
pending_tasks = 0
logs_mb = 0
fatal = none
```

计划任务 `AhzScribe-M5B-Soak` 已删除，隔离 Face/Body 已确认退出，
残留 Face/Body 进程为 0。监控心跳已移除。

### 正式运行

最终候选使用全新目录重新运行，未拼接首次尝试：

```text
输出目录：_local_artifacts/soak/m5b-official-20261101-192431
开始时间：2026-11-01 19:24:35 Asia/Shanghai
结束时间：2026-11-08 19:25:03 Asia/Shanghai
```

最终二进制 SHA-256：

```text
F17A1A4A886F52C3762D6C4F7111CCAFCF552EFACAAB23DC93545C712B5576AD  AhzScribe.Face.exe
1266DBE639C6BEC4BFC244296B2C50B4EA2E320DEE18388880D334104BF86C17  AhzScribe.Body.exe
A29DE70658ACE4831F901A1B6FA9448DB9501E71AA8D6FA6C7E058A805269473  AhzScribe.Brain.dll
A010FA523A466F53158FE94995AD67F5A96EFFF3595F367BC5EA7B147EEFC1C4  AhzScribe.Contracts.dll
```

正式结果：

```text
result = M5B_PASS
qualifies_for_gate = true
duration_hours = 168.0079
samples = 10075
fatal = none
restart_ok = true
restart_detail = face_alive=True body_alive=True health=Healthy
identity_preserved = true
identity_samples = 10075
max_pending_tasks = 0
max_logs_mb = 6.72
baseline_face_working_set_mb = 74.69
final_face_working_set_mb = 38.77
memory_growth_mb = -35.92
max_face_working_set_mb = 85.68
max_face_handles = 556
max_face_threads = 21
max_sample_gap_seconds = 62.451
healthy_ratio = 1.0
```

## 4. 预运行烟测

```text
result = SMOKE_PASS
gate = M5B
duration_hours = 0.0219
samples = 15
fatal = none
restart_ok = true
identity_preserved = true
identity_samples = 15
max_pending_tasks = 0
max_logs_mb = 0
memory_growth_mb = 4.06
healthy_ratio = 1.0
max_sample_gap_seconds = 5.139
```

烟测输出：

```text
_local_artifacts/soak/m5b-smoke-20261101-083909/summary.json
```

## 5. 结束条件

任务完成后必须：

1. 读取 `summary.json`；
2. 确认 `result=M5B_PASS` 且 `identity_preserved=true`；
3. 核对重启检查与进程残留；
4. 运行 `tools/soak/remove-m5b-soak-task.ps1` 删除计划任务；
5. 将本文更新为 PASS 或记录失败证据。

## 6. 当前状态

**M5-b PASS。** 最终候选连续运行 168.0079 小时，10075 次采样全部保持身份，
健康比例 100%，重启恢复为 Healthy。

## 7. 最终候选预检

M1–M8a 最终候选预检已通过：

```text
M8B_PRECHECK_PASS
face_modes = 28/28
clean_target_soul_restore = true
safe_self_evolution = true
open_p0_p1 = 0
release_ready = true
```

预检记录：

```text
docs/acceptance/m8-b-final-preflight.md
```

M5-b 已于 2026-11-08 19:25:03 完成，最终剩余门槛清除。
