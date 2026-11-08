# Ahz Scribe 工程文档

> 版本：2026-10-25
> 范围：`local-agent` 当前实现、S0–S6 阶段成果、S1 审计硬边界
> 说明：本文按实际代码、脚本、测试日志整理；行数和测试条数以当天日志为准。

---

## 1. 项目定位

Ahz Scribe 是一个运行在 Windows 桌面上的本地智能体：

- 可以对话、调用工具、读写文件、运行命令；
- 有状态、有记忆、有身体感；
- 可以在空闲时做脉冲决策；
- 所有工具动作写入可校验审计；
- 桌面操作以 UI Automation 优先，坐标点击兜底。

当前代码仍是**单文件 C# 5 WinForms 应用**，目标架构是后续拆成：

```text
ahz-scribe-brain   推理 / 模型 / 记忆 / 脉冲决策
ahz-scribe-body    工具 / 文件 / Shell / 网络 / 快照 / 审计
ahz-scribe-face    UI / CLI / 投递 / 审批
```

---

## 2. 时间节点与完成内容

| 时间 | 节点 | 完成内容 |
|---|---|---|
| 2026-10-11 | 极限测试基线 | 建立记分卡与护栏基线，确认云端/本地模型差异 |
| 2026-10-13 | Codex 风格 UI | 自绘标题栏、侧栏、输入卡、浮层、消息块体系 |
| 2026-10-15 | 大会话性能 | 打开会话只渲染最近 200 条；流式重排改为增量；消息折叠与提醒限次 |
| 2026-10-16 | 意图澄清 | 意图澄清弹窗、命令绑定、验收文档 |
| 2026-10-18 | UI 回归 | 图片缩略图、工具图、控件几何回归 |
| 2026-10-24 | S0 状态层 | `current_state.json`、active_task 租约、open_intents、崩溃恢复 |
| 2026-10-24 | S1 审计 | JSONL 审计、payload、sha256、CLI、急停 |
| 2026-10-24 | S2 记忆 | semantic / episodic / disputed 三轨、检索、注入、后台提取 |
| 2026-10-24 | S3 身体感 | 30 秒采样、前台窗口、空闲、CPU/内存/磁盘/网络/电池、摘要注入 |
| 2026-10-24 | S4 脉冲 | 定时器、body_event、active_task 优先、none 合法 |
| 2026-10-24 | S5 主动性 | PulseDecision 解析、静默时段、每日上限、未回复暂停、MessageSink |
| 2026-10-24 | S6 桌面 | 截图、窗口枚举、聚焦、坐标点击、输入、按键 |
| 2026-10-24 | QQ 桥 | NapCat/OneBot 11 + Python 桥；owner 校验、指令收件箱、截图回传 |
| 2026-10-25 | S6 UIA | `desktop_uia_dump/find/invoke/set_value/focus` |
| 2026-10-25 | S4 意图层 | 结构化 `open_intents.json`、到期检查、脉冲队列、模型决策、每日内驱 |
| 2026-10-25 | S4 真实试跑 | 到期意图 → `intent_due` → 模型 `none` → 审计 → 意图 done |
| 2026-10-25 | S5 真实试跑 | 到期意图 → 模型 `message` → 投递到界面 → 审计 `sent` |
| 2026-10-25 | S1 硬边界 | ProgramData 审计 ACL、只追加、哈希链、`harden-audit.ps1` |
| 2026-10-25 | S1 broker | `AhzAuditBroker` Windows 服务、LocalSystem、命名管道、HMAC 密钥 |

> 早期 UI 与测试节点的细节见 `AGENTS.md`、`AHZScribe-修复复验-2026-10-18.md`、`devtools/极限测试报告-2026-10-11.md`。

---

## 3. 技术栈

### 3.1 客户端

| 技术 | 用途 |
|---|---|
| C# 5 | 系统自带 `csc.exe` 可编译，不依赖新 SDK |
| .NET Framework 4.8 | 旧栈运行时 |
| WinForms | 主窗口、设置页、浮层、消息块 |
| 自绘控件 | 标题栏、侧栏、模型 chip、滚动条、消息 transcript |
| CefSharp | 内置浏览器（可选） |
| Scintilla.NET | 编辑器输入条（可选） |
| `System.Text.Json` / `JavaScriptSerializer` | 配置、状态、审计 JSON |
| `System.Threading.Timer` | 身体采样、脉冲调度 |
| `System.Net.Http` | OpenAI 兼容接口 / Ollama |
| `System.Windows.Automation` | UI Automation 控件树、Invoke、ValuePattern |
| P/Invoke `user32` / `kernel32` | 窗口枚举、鼠标键盘、追加写审计 |

### 3.2 审计与安全

| 技术 | 用途 |
|---|---|
| JSONL | append-only 事件流 |
| SHA-256 | 审计链 `prev_hash` / `entry_hash` |
| HMAC-SHA256 | broker 签名，密钥只给 SYSTEM/Administrators |
| NTFS ACL | 普通用户只能 `Read + Append` 主审计文件 |
| Windows 服务 | `AhzAuditBroker` 以 LocalSystem 运行 |
| Named Pipe | `ahz-scribe-audit`，应用与服务通信 |
| Windows UIAutomation | 控件级桌面操作，坐标点击兜底 |

### 3.3 外部集成

| 技术 | 用途 |
|---|---|
| NapCat / QQNT | QQ 机器人 |
| OneBot 11 HTTP | 事件上报、发送消息/图片 |
| Python 标准库 HTTP | 本地 QQ 桥、指令收件箱 |
| Python PIL | 截图 |
| DeepSeek / OpenAI 兼容 API | 模型调用 |
| Ollama | 本地模型 |

---

## 4. 当前架构

```text
┌─────────────────────────────────────────────┐
│              AhzScribe.exe (WinForms)       │
│  UI / AgentRunner / Tools / Memory / Pulse  │
└───────────────┬─────────────────────────────┘
                │ named pipe
                ▼
┌─────────────────────────────────────────────┐
│      AhzAuditBroker.exe (LocalSystem)       │
│  append + hash chain + HMAC                 │
└───────────────┬─────────────────────────────┘
                ▼
C:\ProgramData\AhzScribe\audit\
  audit.jsonl     只追加 + HMAC
  audit.key       仅 SYSTEM/Administrators
  payloads\       完整参数
```

本地数据：

```text
%LOCALAPPDATA%\AhzScribe\
  state\current_state.json
  state\open_intents.json
  state\body_state.json
  memory\semantic.jsonl
  memory\episodic.jsonl
  memory\disputed.jsonl
```

---

## 5. 已实现能力

### S0 状态层

- `current_state.json` 持久化；
- `active_task` 带租约，崩溃后过期释放；
- `open_intents`、`pending_confirmations`、`last_pulse_at`；
- 会话/审计关联。

### S1 审计

- `audit.jsonl` + `payloads/`；
- 参数脱敏、sha256、payload 引用；
- `--audit tail/show/export/verify`；
- 急停文件；
- 审计链哈希；
- ProgramData ACL 只允许普通用户追加；
- LocalSystem broker + HMAC 密钥。

### S2 记忆

- semantic / episodic / disputed 三轨 JSONL；
- 检索、注入上下文；
- `source_audit_id`；
- 矛盾记忆标记；
- 后台提取线程。

### S3 身体感

- 30 秒采样；
- 前台应用/标题、空闲秒数、锁屏；
- CPU、内存、磁盘、网络、电池；
- `body_state.json`；
- 摘要注入系统提示。

### S4 脉冲

- `PulseTrigger` 队列；
- 优先级：`intent_due > body_event > drive > timer`；
- active_task 让路；
- 结构化意图：
  - `time_after`
  - `manual`
  - `memory_key`
- 每 10 分钟检查到期；
- 每日内驱：矛盾 / 证据缺口 / 结晶不足 / 悬空边四类候选，按 0.4/0.3/0.2/0.1 评分，每天最多一个；
- 模型输出 `none / message / wait`；
- 意图完成后状态 `done`。

### S5 主动性

- `PulseDecision.Parse`；
- 静默时段；
- 每日主动消息上限；
- 连续两次未回复暂停 24 小时；
- `MessageSink` 投递到 UI；
- 审计 `pulse_model` / `proactive_message`。

### S6 桌面

- `desktop_screenshot`
- `desktop_list_windows`
- `desktop_focus_window`
- `desktop_click`
- `desktop_type`
- `desktop_key`
- `desktop_uia_dump`
- `desktop_uia_find`
- `desktop_uia_invoke`
- `desktop_uia_set_value`
- `desktop_uia_focus`

安全：点击/输入/UIA 写操作需要审批；只读/计划模式拦截。

---

## 6. 验证记录

### 6.1 全量自测

日志：`_build/final-ui-tests-111.log`

```text
流式重画：345 项通过，0 失败
规矩/验证：293 项通过，0 失败
设置页：93 项通过，0 失败
最小化/还原：22 项通过，0 失败
控件几何：47 个可见控件，越界 0
```

### 6.2 S4 真实脉冲

```text
intent_pulse_test_003
→ intent_due
→ pulse_model: none
→ pulse: decision none
→ intent status: done
```

### 6.3 S5 真实主动消息

```text
intent_s5_message_001
→ pulse_model: message
→ proactive_message: sent
→ 界面收到消息
→ intent status: done
```

审计链：

```text
审计链通过（HMAC 校验）
```

### 6.4 S2 / S3 真实试跑

S2 记忆提取（独立临时数据目录）：

```text
输入：请记住：我的项目代号是蓝鲸……
semantic.jsonl：2 条记录
跨进程再问：蓝鲸指什么？
回答：Ahz Scribe 最终版
```

S3 工作区状态（设置页自测第 18 节）：

```text
干净 git 工作区不 dirty     PASS
有未提交修改就 dirty        PASS
```

### 6.5 S6 真实 UIA（多软件）

2026-10-25 对三个真实窗口执行 `desktop_uia_dump`：

```text
Notepad       Window / Pane / Document / Tab / MenuBar / MenuItem / Button
文件资源管理器 Window / Pane / Tab / SplitButton / Group / TitleBar / MenuBar / Button
Microsoft Edge Window / Pane / Text（仅顶层，Chromium 未暴露页面控件）
Word          Window / TitleBar / MenuItem / Button / BackstageView（产品激活失败）
Excel         Window / TitleBar / ComboBox / Edit / List（产品激活失败 + 激活向导）
QQ            Pane / Document "Chrome Legacy Window"（仅 Chromium 容器，无可操作控件）
```

结论：

- Win32 / UWP 类窗口能读到有效控件；
- Explorer 能读到地址栏、搜索框、命令栏的 AutomationId；
- Edge/Chromium 只暴露顶层 Pane，不能依赖 UIA 操作页面内容；
- 浏览器场景仍应走内置浏览器 DOM，而不是硬点坐标。

QQ 适配（UIA 不可用）：qq_window / qq_click / qq_type / qq_key / qq_send，用窗口相对坐标和快捷键；点击/输入/发送走审批。已在豆包聊天完成一次真实发送（「Ahz Scribe QQ 适配测试，请忽略。」），机器人已回复。
- Edge 标题里存在零宽空格；UiaText 已做归一化，title=Microsoft Edge 可以匹配。

### 6.6 S1 硬边界

```text
普通用户追加         OK
普通用户覆盖         DENIED
普通用户删除         DENIED
普通用户读 audit.key DENIED
服务状态             RUNNING
```

### 6.6.1 body v0

```text
contracts: 0 warnings / 0 errors
body:      0 warnings / 0 errors
BODY SELF-TEST: PASS
BODY IPC: PASS
BRAIN CLIENT SMOKE: PASS
```

### 6.7 .NET 10 正式构建

```text
JsonSer: System.Text.Json (NET10_0_OR_GREATER)
UIAutomation: WindowsDesktop 10.0 refs
CefSharp / Scintilla: 本地 DLL 引用
BUILD: 0 errors / 8 warnings
APP: 启动后进程 Responding（未做 UI 全量回归）
```

---

## 7. 当前未完成项

1. **payloads 已改为普通用户只读，broker（SYSTEM）写入**；但仍不是单文件 append-only 流。
2. **Agent 可向 broker 追加伪造条目**：不能改已有链，但追加本身无法区分；需要更高层语义校验/人工复核。
3. **broker 停机时**应用退回本地 ACL 追加，此时只有哈希链、没有 HMAC；校验会提示“仅本地哈希”。
4. **S2 真实 API 提取**已用两轮跨进程试跑验证；尚未做长期高频运行。
5. **S3 `workspace_dirty` 已接入 git status**；非 git 目录仍返回 false。
6. **S6 已实测 Notepad / Explorer / Edge / Word / Excel / QQ**；Edge/QQ 只暴露 Chromium 容器，QQ 已补相对坐标/快捷键适配器，Office 受激活失败影响。
7. **.NET 10 正式 PackageReference 构建已通过**（0 errors，应用可启动）。
8. **新项目 brain/body/face 仅有目录契约**，实际代码仍在单文件 `local-agent/src/AhzScribe.cs`。
9. **开源未执行**：按用户要求等项目完成后再开。

---

## 8. 工程文件与脚本

| 文件 | 作用 |
|---|---|
| `src/AhzScribe.cs` | 主源码（含 `#if AUDIT_BROKER` 服务入口） |
| `build.cmd` | 编译 GUI/CLI 旧栈 |
| `ahzscribe-cli.exe` | 命令行版；`--audit verify` 等 |
| `bin/AuditBroker.exe` | 审计服务 |
| `devtools/build-audit-broker.cmd` | 编译 broker |
| `devtools/harden-audit.ps1` | 设置 ProgramData 审计 ACL |
| `devtools/install-audit-broker.ps1` | 安装/更新服务（管理员） |
| `devtools/uninstall-audit-broker.ps1` | 卸载服务（管理员） |
| `devtools/bench/run-ui-tests.cmd` | 全量 UI/规则/设置/几何测试 |
| `devtools/bench/net10-smoke.ps1` | .NET 10 构建 + 启动冒烟 |
| `migration/AhzScribe.Net10/` | .NET 10 PackageReference 迁移项目（还原阻塞） |
| `migration/AhzScribe.Net10.LocalDeps/` | .NET 10 本地依赖构建（已通过，0 errors） |

---

## 9. 运行与维护

### 首次安装审计边界

```powershell
# 管理员
.\devtools\harden-audit.ps1
.\devtools\install-audit-broker.ps1
```

### 验证审计

```bat
ahzscribe-cli.exe --audit verify
```

### 卸载 broker

```powershell
# 管理员；审计文件保留
.\devtools\uninstall-audit-broker.ps1
```

### 注意

- `audit.key` 普通用户不可读；丢失后旧 HMAC 无法验证，换钥前先备份。
- `audit.jsonl` 只能追加；需要归档时用复制，不要 `Move`/`Delete`。
- broker 服务停止时应用仍能运行，但审计降级为本地哈希模式。

---

## 10. 下一阶段建议

```text
1. 继续收紧 payloads（单文件 append-only 流）
2. .NET 10 的 UI/功能全量回归（构建与应用启动已通过）
3. brain/body/face 实际拆分
4. S6 多软件 UIA 适配
5. 开源前安全复核与文档整理
```

---

## 11. 结论

当前版本已经具备：

- 可对话、可调工具；
- 有状态、有记忆、有身体感；
- 有结构化意图与真实脉冲闭环；
- 有真实主动消息投递；
- 有界面级 UIA 能力；
- 有 NTFS ACL + 独立 broker + HMAC 的审计硬边界；
- 有全量回归测试。

它已经是一个**可个人使用的本地智能体**，但还不是“全部软件适配、全部平台复测、代码完全拆分”的最终形态。
