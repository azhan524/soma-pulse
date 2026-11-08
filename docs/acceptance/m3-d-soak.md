# M3-d 72 小时稳态测试验收记录

> 日期：2026-10-28
> 阶段：M3-d
> 结果：PASS
> 场景：隔离 body + face 连续运行 72 小时

---

## 1. 目标

验证连续运行期间：

- face / body 不崩溃；
- 内存增长有界；
- 待发送队列不超过 8；
- 日志和记忆体积保持在预算内；
- 健康状态至少 95% 的采样为 `Healthy`；
- 停止后可用同一数据目录重新启动并恢复健康状态。

## 2. 测试工具

```text
tools/soak/m3d-soak.ps1
```

工具使用独立 `AHZ_FACE_DATA`、`AHZ_BODY_DATA`、`AHZ_BODY_PIPE` 和
`AHZ_STOP_FILE`；不会接触真实 `data/`。

## 3. 采样

默认每 60 秒采样一次：

```text
face / body 存活
Working Set / Private Bytes
句柄数 / 线程数
face 数据目录体积
memory / logs 体积
health state
active tasks / pending tasks
hourly tool calls / retries
```

## 4. 通过门槛

```text
实际持续时间 >= 71.9 小时
采样数 >= 2
无进程提前退出
最大 pending tasks <= 8
最大 logs <= 32 MiB
Working Set 增长 <= 512 MiB
Healthy 采样比例 >= 95%
重启后 face / body 存活且 health.json 可读
```

## 5. 产物

```text
metrics.jsonl
status.json
summary.json
soak.log
face.stdout.log / face.stderr.log
body.stdout.log / body.stderr.log
```

## 6. 结论

短时烟测：

```text
result = SMOKE_PASS
samples = 4
duration_hours = 0.0065
memory_growth_mb = 1.48
max_pending_tasks = 0
max_logs_mb = 0
healthy_ratio = 1.0
restart = PASS
```

正式运行：

```text
开始：2026-10-29 07:51 Asia/Shanghai
实际结束：2026-11-01 07:51
时长：72 小时
采样：每 60 秒
输出：_local_artifacts/soak/m3d-official-20261029-075119
当前状态：PASS
```

soak 进程使用独立 `AHZ_INSTANCE_ID`，不会占用用户正常 Face 的单实例入口。
`start.ps1` 改为按默认命名管道判断，不会把隔离 soak 误认为正式 body / face。
正式运行由 Windows 计划任务 `AhzScribe-M3D-Soak` 托管，使其不依赖 Codex/ChatGPT 进程树。

采样间隔超过 185 秒会判定为中断，防止系统休眠或采样器卡住被误算作连续运行。

中断记录：

```text
第 1 次正式尝试：2026-10-28 19:33
运行到 sample=41 / 40.059 分钟后终止
原因：Windows Update 安装并启动 ChatGPT 服务，原 Codex 子进程树被结束
判定：环境更新导致的任务树退出，不计为 M3-d PASS

第 2 次正式尝试：2026-10-28 20:21
运行到 sample=680 / 679.367 分钟后终止
原因：Windows Update 安装 2026-10 预览更新 KB5127936，
      MoUsoCoreWorker.exe 与 TrustedInstaller.exe 先后发起计划重启
判定：系统重启打断连续运行，不计为 M3-d PASS
```

待正式运行结束后，根据 `summary.json` 填写 M3-d 最终 PASS / FAIL。

最终结果：

```text
result = M3D_PASS
qualifies_for_m3d = true
duration_hours = 72.0014
samples = 4318
fatal = none
restart_ok = true
restart_health = Healthy

max_pending_tasks = 0
max_logs_mb = 2.88
baseline_face_working_set_mb = 71.72
final_face_working_set_mb = 23.64
memory_growth_mb = -48.08
max_face_working_set_mb = 75.87
max_face_handles = 552
max_face_threads = 22
max_sample_gap_seconds = 61.263
healthy_ratio = 1.0
```

结论：

- 连续运行超过 72 小时；
- Face / Body 没有崩溃；
- 4318 次采样全部健康；
- 队列最大值 0；
- 日志最大 2.88 MiB，低于 32 MiB 门槛；
- Face Working Set 未增长，最终比初始低 48.08 MiB；
- 最大采样间隔 61.263 秒，未发生休眠或采样中断；
- 停止后使用同一数据目录重启成功，健康状态恢复为 `Healthy`。

临时计划任务 `AhzScribe-M3D-Soak` 和对应监控心跳已清理。
