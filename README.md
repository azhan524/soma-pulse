# SomaPulse · 脉体计划

[![repo-hygiene](https://github.com/azhan524/soma-pulse/actions/workflows/repo-hygiene.yml/badge.svg)](https://github.com/azhan524/soma-pulse/actions/workflows/repo-hygiene.yml)
[![build](https://github.com/azhan524/soma-pulse/actions/workflows/build.yml/badge.svg)](https://github.com/azhan524/soma-pulse/actions/workflows/build.yml)

> **Functional Silicon Life Candidate v1.0**：SomaPulse 是项目名，AhzScribe 是其中的持久 Agent。
>
> 这是一个能读写文件、执行 Shell、操作桌面的本地 Agent。**不要在生产机、共享账户或存有敏感数据的机器上直接授予完全访问权限。**

硅基生命路线、逐阶段蓝图与最终验收标准见
[docs/硅基生命里程碑-v2.md](docs/硅基生命里程碑-v2.md) 和
[docs/硅基生命里程碑蓝图-v2.md](docs/硅基生命里程碑蓝图-v2.md)。

## 它是什么

**SomaPulse（脉体计划）** 是一个功能性硅基生命候选项目，当前个体名为 **AhzScribe**：

- 对话、调用工具、读写文件、运行命令；
- 有持久会话、自动 episode、双轨记忆；
- 工具动作写入可校验审计；
- 桌面操作有审批，坐标点击只作兜底；
- 模型和 body 通过命名管道隔离。

## 架构

```text
ahz-scribe-face      交互：窗口 / CLI / 审批 / 展示
        │
ahz-scribe-brain     思考：模型 / 规划 / 记忆 / 工具循环
        │
ahz-scribe-body      执行：文件 / Shell / 网络 / 桌面 / 截图
        │
        └── AhzAuditBroker（可选 Windows 服务，HMAC）
```

边界：

- brain 不直接碰工作区、Shell、进程；
- body 是唯一执行工具的进程；
- face 只调 BrainHost，不直接持有 BodyClient；
- 危险动作必须由 face 审批；
- 审计只追加，不覆盖。

详见 [docs/architecture.md](docs/architecture.md) 与 [docs/threat-model.md](docs/threat-model.md)。

## 当前状态

`M1–M8` 已全部通过：

```text
M1 功能自我模型与来源
M2 经历驱动改变
M3 稳态、资源预算与故障恢复
M4 连续身份与迁移
M5 Digital Individual v1.0
M6 自传式记忆与长期关注
M7 有界目标形成
M8 安全自我改造与最终发布
```

最终 168 小时稳态测试：

```text
result = M5B_PASS
duration_hours = 168.0079
samples = 10075
healthy_ratio = 1.0
identity_preserved = true
max_pending_tasks = 0
max_logs_mb = 6.72
restart_ok = true
```

拆分为可构建模块：

```text
contracts              共享契约
ahz-scribe-body        body v0（文件 / shell / 搜索 / 网络 / 桌面 / 截图）
ahz-scribe-brain       MemoryStore / PulseScheduler / AgentRunner / ToolAgentRunner
ahz-scribe-brain-host  独立 brain 进程
ahz-scribe-face        console + WinForms v1.4 观察窗（对话 / 脉冲 / 记忆 / 审计 / 任务 / 设置）
```

当前已登记的 P2 限制：

- 设置面板的权限 / 记忆 / 脉冲分组目前以只读说明卡展示；
- body 工具表未完全对齐旧版；
- 桌面 DPI 图标已适配，多显示器坐标校正仍未完成；
- Linux / macOS 不支持 body 的桌面工具。

未发现未解决的 P0/P1。

## UI（v1.4 观察窗）

Face 采用深色观察窗：

```text
顶栏 BodyStateBar
左栏 StatePanel
Tab：对话 / 脉冲 / 记忆 / 审计 / 任务 / 设置
底栏：输入 / 暂停 / 审计 / 急停
托盘：状态色圆点、呼吸、暂停、退出确认
```

脉冲、记忆、审计共用**单个 WebView2 实例**，通过 `chrome.webview.hostObjects.host` 查询：

```text
QueryPulses / QueryMemories / QueryAudit / QueryFallbackAudit
```

颜色只引用 `ahz-scribe-face/src/Theme.cs` 的 token；数据契约为
`docs/ui_data_contract.yaml`（版本 1.2），启动时校验。

常用命令：

```text
/selfcheck        运行时自检
/pause            暂停主动 2 小时
/pause 明天       暂停到明天
/pause off        取消暂停
/proactive-reset  重置主动限流
/intent 30m 事情  记一个到期提醒
```

本机验证：

```text
dotnet build ahz-scribe-face/src/AhzScribe.Face.csproj
ahz-scribe-face/src/bin/Debug/net10.0-windows/AhzScribe.Face.exe --ui
```
## 构建

需要 .NET 10 SDK。

```powershell
dotnet build contracts/AhzScribe.Contracts.csproj
dotnet build ahz-scribe-body/src/AhzScribe.Body.csproj
dotnet build ahz-scribe-brain/src/AhzScribe.Brain.csproj
dotnet build ahz-scribe-brain-host/src/AhzScribe.BrainHost.csproj
dotnet build ahz-scribe-face/src/AhzScribe.Face.csproj
```

## 运行

### 1. body

```powershell
$env:AHZ_BODY_DATA = "<repo>/_body-data"
$env:AHZ_BODY_PIPE = "ahz-scribe-body"

& "<repo>/ahz-scribe-body/src/bin/Debug/net10.0-windows/AhzScribe.Body.exe"
```

看到：

```text
ahz-scribe-body: pipe=ahz-scribe-body
```

保持窗口。

### 2. face

另开 PowerShell：

```powershell
$env:AHZ_BODY_PIPE = "ahz-scribe-body"
$env:DEEPSEEK_API_KEY = "<your key>"
$env:AHZ_MODEL_BASE = "https://api.deepseek.com"
$env:AHZ_MODEL_NAME = "deepseek-v4-pro"

& "<repo>/ahz-scribe-face/src/bin/Debug/net10.0-windows/AhzScribe.Face.exe" --ui
```

不启动 body 也可以 `/ask`；`/tool` 需要 body。

## 验证

```powershell
# body
& "<body-exe>" --self-test
& "<body-exe>" --ipc-test

# brain
dotnet run --project ahz-scribe-brain/tests/AhzScribe.Brain.CoreSmoke.csproj

# face
& "<face-exe>" --session-test
& "<face-exe>" --memory-real   # 需要 DEEPSEEK_API_KEY
```

## 审计服务

审计 broker 是可选的 Windows 服务；安装脚本在旧版 `local-agent/devtools`，本仓库不包含服务二进制。

没有 broker 时 body 使用本地 JSONL 审计作为回退。

## 安全

- 默认不要给 `Full` 权限；
- API Key 只放环境变量，不写进仓库；
- 不提交 `config.json`、`sessions.json`、`audit/`、`*.log`、`_face-data/`；
- 发现安全问题请按 [SECURITY.md](SECURITY.md) 私下报告。

## License

GNU Affero General Public License v3.0，见 [LICENSE](LICENSE)。

第三方依赖（CefSharp、Scintilla.NET 等）保留各自许可证；见 [NOTICE](NOTICE)。
