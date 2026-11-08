# M5-a 发布候选审计验收记录

> 日期：2026-11-01
> 阶段：M5-a
> 结果：PASS
> 场景：M1–M4 验收矩阵、阻塞问题与全量自动回归

---

## 1. 目标

把 M1–M4 的分散验收收口为固定发布闸门，确认当前源码具备进入 168 小时连续运行和设备/模型迁移实测的资格。

本阶段不宣称 M5 已完成，也不宣称产品已达到 Digital Individual v1.0。

## 2. 预注册判据

- M1–M4 共 15 份验收记录存在且结果为 PASS；
- 问题登记表中没有未关闭的 P0/P1；
- contracts、brain、body、brain-host、face 五个项目全部构建成功；
- Brain 核心回归通过；
- Body 自测与 IPC 通过；
- 24 个 Face 测试模式通过；
- 测试结束后没有残留 Face/Body/BrainHost 进程；
- 输出机器可读 `release-report.json`；
- 闸门只能判定为“可进入 M5-b”，不得提前设置 `release_ready=true`。

## 3. 固定命令

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File tools/release/m5-release-verify.ps1
```

脚本会查找 `AHZ_DOTNET`、`DOTNET_ROOT` 或 PATH 中的 `dotnet`，并要求至少存在一个可用 SDK。

## 4. 实测结果

```text
M5A_PASS
acceptance_docs = True
required_docs = 15
open_p0_p1 = 0
brain_core = True
body_self_test = True
body_ipc = True
face_modes = 24/24
residue_processes = 0
release_ready = False
```

构建结果：

```text
contracts   0 warnings / 0 errors
brain       0 warnings / 0 errors
body        0 warnings / 0 errors
brain-host  0 warnings / 0 errors
face        1 known warning / 0 errors
```

问题登记：

```text
P0 open = 0
P1 open = 0
P2 open = 3
```

P2 均来自已知边界：

- WebView2 的 `WindowsBase` 构建警告；
- 多显示器坐标校正尚未完成；
- body 工具表尚未与历史实现完全对齐。

## 5. 产物

正式本地产物：

```text
_local_artifacts/release/m5a-final-20261101-084035/
  release-report.json
  release-verify.log
  build-*.log
  brain-core.log
  body-self-test.log
  body-ipc-test.log
  face-*.log
```

`release-report.json` 记录：

- SDK 版本；
- Git 提交与工作区状态；
- 每项构建和测试的退出码；
- 验收文档与问题登记检查结果；
- Face 测试通过数；
- 残留进程数；
- 下一闸门 `M5-b 168-hour soak`。

本次源码工作区含大量尚未提交的 M1–M4 实现，因此报告如实记录 `source_clean=false`。这不阻塞 M5-a 的预发布验证，但正式发布前必须提交并重新运行闸门取得干净源码基线。

## 6. 已知边界

- M5-a 只证明自动回归和问题矩阵满足进入下一闸门；
- 7 天连续运行尚未完成；
- 三次真实迁移序列和急停恢复仍需 M5-c 复验；
- 当前机器没有常驻 .NET SDK，本轮使用临时官方 .NET 10.0.403 SDK 验证。

## 7. 结论

M5-a 已通过。当前构建可以进入 M5-b；在 M5-b/M5-c 完成前不得把产品标为 Digital Individual v1.0。

当前状态：**M5-a PASS；M5-c 已通过；M5-b 已按用户要求延期。**
