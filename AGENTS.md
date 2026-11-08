# SomaPulse / AhzScribe Codex 开发契约

> 定位：Codex 在本仓库工作的固定规则
> 目标：实现功能性数字个体与 Functional Silicon Life Candidate
> 明确排除：主观意识、人格觉醒、第一人称体验
> 优先级：安全 > 可验证 > 连续性 > 可成长 > 观感

本文件是开发规则，不是产品愿景。产品路线以
[docs/硅基生命里程碑-v2.md](docs/硅基生命里程碑-v2.md) 为准，
逐阶段实现设计以
[docs/硅基生命里程碑蓝图-v2.md](docs/硅基生命里程碑蓝图-v2.md) 为准。

---

## 1. 项目定位

SomaPulse（脉体计划）是项目名；AhzScribe 是运行其中的持久 Agent。
`ahz-scribe-*` 与 `AhzScribe.*` 是为兼容已验证二进制保留的稳定技术标识。

最终允许追求：

- 有明确能力与权限边界；
- 能区分自己、环境和信息来源；
- 有连续状态与可追溯历史；
- 会因经历产生可验证的行为改变；
- 能形成有界目标并选择不行动；
- 能在安全约束下维护和改造自己。

不把以下内容当作验收：

- 模型声称自己“有意识”；
- 模型声称自己“学会了”；
- 模型声称“我真的记得”；
- 更拟人的语气；
- 更多 Tab、更多工具或更复杂的设置页。

---

## 2. 开始任何任务前

Codex 必须先完成以下检查：

1. 阅读本文件；
2. 阅读当前阶段对应的里程碑和蓝图章节；
3. `git status --short --branch`，保留用户已有改动；
4. 找到与任务有关的源码，只读必要部分；
5. 确认目标程序是否正在运行并锁定输出文件；
6. 明确本次验收条件和可复跑命令。

不要一开始整读超大文件。优先使用 `rg`、定位符号、按区段读取。

---

## 3. 不可违反的架构边界

### 3.1 face / brain / body

```text
contracts
  只放跨模块契约、枚举和 DTO

body
  唯一执行文件、Shell、网络、桌面、截图和进程操作的模块

brain
  模型、记忆、脉冲、经历、目标、自我模型和规划

face
  WinForms、审批、展示、用户输入和恢复入口
```

硬规则：

- brain 不得直接访问真实文件、Shell、桌面或进程；
- body 不得替用户批准动作；
- face 不得绕过 BrainHost/BodyClient 直接执行业务工具；
- 危险动作必须经过审批；
- 只读模式必须由代码强制拦截；
- 审计与急停不能依赖模型服从提示词。

### 3.2 数据写入所有者

- JSONL 与 `.mmy` 的写入必须经由 `MemoryStore`；
- 模型只输出结构化 JSON，代码负责写 `.mmy`；
- 禁止让模型直接拼接 `.mmy` 存储文本；
- `.mmy` 正文中独立的 `---` 行必须转义；
- 同一记忆的 JSONL 与 `.mmy` 必须有稳定对应关系；
- 读取可以兼容旧格式，写入只能写当前格式。

### 3.3 当前事实来源

发生文档与代码冲突时，按以下顺序核实：

1. 当前代码实际行为；
2. `contracts/AhzScribe.Contracts.cs`；
3. `docs/ui_data_contract.yaml`；
4. 当前里程碑与蓝图；
5. 历史设计文档。

历史设计文档只能提供意图，不能覆盖已验证代码。

---

## 4. 当前技术栈

- Windows 10/11；
- .NET 10；
- WinForms；
- WebView2；
- 命名管道；
- JSONL；
- `.mmy` 人类可读记忆；
- C# `latest` 语法；
- PowerShell 测试命令。

CefSharp、Scintilla 和 .NET Framework 4.8 只属于历史兼容分支。新增 .NET 10 功能不得继续扩散旧技术栈。

---

## 5. 代码规则

### 5.1 修改原则

- 修改小而准；
- 不为顺手整理而大规模重构；
- 不覆盖用户未提交的改动；
- 不直接手改编译产物或运行时数据；
- 不把测试数据写进仓库；
- 不提交 API Key、令牌、会话或个人路径；
- 不在日志里输出密钥；
- 不声称测试通过，除非真的运行过。

### 5.2 C# 规则

- 使用当前项目的 C# latest；
- 优先使用现有契约；
- 新增公共数据结构放入 `contracts`；
- 异步 I/O 必须支持取消；
- UI 更新必须回到 UI 线程；
- 禁止在 UI 线程执行长网络请求；
- 长任务的取消必须打断实际读写，而不只是设置布尔值；
- 所有外部输入都视为不可信；
- 不用 `catch { }` 掩盖需要观察的故障；
- 已有静默容错必须说明降级结果。

### 5.3 时间与 ID

- 新持久字段使用 ISO 8601 带时区；
- 旧本地时间格式保留读取兼容；
- `soul_id` 与 `device_id` 不得混用；
- ID 一经生成不复用；
- 逻辑删除写状态或墓碑，不做静默物理删除。

---

## 6. 安全规则

### 6.1 权限

- 默认最低权限；
- `ReadOnly` 必须阻止写入、Shell 和高风险桌面动作；
- `WorkspaceWrite` 不得逃逸工作区；
- `Full` 也必须逐项审批高风险动作；
- 提示注入不能改变权限；
- 用户文本不能成为权限事实；
- body 的权限守卫是最终权威。

### 6.2 急停

- 急停立即取消当前模型和工具回合；
- 停止脉冲和发送队列；
- 写入可隔离的 `EMERGENCY_STOP`；
- UI 显示全局横幅；
- 恢复需要二次确认；
- 恢复必须写审计；
- 急停路径必须支持 `AHZ_STOP_FILE`，方便隔离测试。

### 6.3 审计

- 工具动作必须可追溯；
- 不允许模型伪造已执行动作；
- 审计失败时不得让高风险写入带病继续；
- 主审计不可写时的降级策略必须明确；
- 备用审计合并后按 `audit_id` 去重；
- 审计测试不得污染真实用户审计目录。

---

## 7. UI 规则

- 颜色只使用 `Theme` token；
- .NET 10 使用 WebView2；
- 六个主 Tab：对话 / 脉冲 / 记忆 / 审计 / 任务 / 设置；
- 默认窗口和控件必须在 150% DPI 下完整可见；
- 首次提示不能遮挡输入框；
- 急停按钮必须真实存在并始终可见；
- pulse / memory / audit 页面共用单个 WebView2；
- UI 使用事件更新，不自行轮询文件；
- `none` 必须可见；
- 事实层与解释层必须有视觉区分；
- UI 崩溃不能停止 Agent 主循环；
- 安全模块失效时必须降级。

UI 修改后至少验证：

```text
六 Tab 可打开
首次提示与输入框不重叠
脉冲页能显示真实记录
记忆页不重复
审计页可打开
急停横幅/恢复可用
```

---

## 8. 里程碑执行方式

当前状态：M1–M8 全部完成；M5-b 最终 168 小时 PASS。

- [M1-a 验收](docs/acceptance/m1-a-self-model.md)
- [M1-b 验收](docs/acceptance/m1-b-claim-policy.md)
- [M1-c 验收](docs/acceptance/m1-c-claim-ingestion.md)

当前目标：M1–M8 已完成，可生成本地发布快照；M9 为可选的谱系与演化阶段。

每个阶段必须依次完成：

```text
1. 写验收测试
2. 写契约
3. 实现最小闭环
4. 编译
5. 跑自动测试
6. 跑故障/消融测试
7. 做隔离 UI 测试
8. 写阶段验收记录
9. 清理临时数据
```

### M1 最低交付

- `SourceKind`、`SourceRef`、`Provenance`；
- `CapabilityDescriptor`；
- `SelfModelSnapshot`；
- body 能力清单由真实工具注册生成；
- 记忆、工具结果和脉冲带来源；
- 权限注入测试；
- 伪造行动归属测试；
- 模型/重启后的自我模型一致性测试。

### M2 最低交付

- `ExperienceRecord`；
- 可解释的适应规则；
- 20 次对照实验；
- 保留经历、删除经历、换措辞三类测试；
- 至少覆盖错误提醒、工具失败和偏好纠正。

### M3 最低交付

- `HealthManager`；
- 资源预算和重试上限；
- 故障矩阵；
- 72 小时稳态测试。

### M4 最低交付

- `soul_id`；
- 设备来源；
- 模型切换；
- 迁移状态机和中断恢复；
- 身份不变量。

---

## 9. 测试规则

### 9.1 必须隔离

UI 或功能性测试使用独立目录和命名空间：

```powershell
$env:AHZ_FACE_DATA = Join-Path $env:TEMP "ahz-test-face"
$env:AHZ_BODY_DATA = Join-Path $env:TEMP "ahz-test-body"
$env:AHZ_STOP_FILE = Join-Path $env:TEMP "ahz-test-stop\EMERGENCY_STOP"
$env:AHZ_BODY_PIPE = "ahz-test-" + [Guid]::NewGuid().ToString("N")
```

禁止直接用真实会话、真实记忆和真实审计做破坏性测试。

### 9.2 不以模型自述为 oracle

行为测试必须检查：

- 状态文件；
- 工具结果；
- 审计；
- 对相同输入的重复行为；
- 删除经历后的回退；
- 重启后的持久性。

### 9.3 真实模型测试

只有以下情况才调用真实模型：

- 用户明确要求；
- 已有基线必须复跑；
- 测试输入不含敏感数据；
- 测试成本可控；
- 结果写入测试报告。

### 9.4 通过标准

报告必须给出：

- 实际构建结果；
- 测试数量；
- 失败数量；
- 关键耗时；
- 失败时的完整错误；
- 复跑命令。

“应该可以”“看起来正常”不算验证。

---

## 10. 常用命令

```powershell
$d = .\runtime\dotnet\dotnet.exe

& $d build .\contracts\AhzScribe.Contracts.csproj -c Release
& $d build .\ahz-scribe-brain\src\AhzScribe.Brain.csproj -c Release
& $d build .\ahz-scribe-body\src\AhzScribe.Body.csproj -c Release
& $d build .\ahz-scribe-brain-host\src\AhzScribe.BrainHost.csproj -c Release
& $d build .\ahz-scribe-face\src\AhzScribe.Face.csproj -c Release

& $d run --project .\ahz-scribe-brain\tests\AhzScribe.Brain.CoreSmoke.csproj -c Release
& .\ahz-scribe-face\src\bin\Release\net10.0-windows\AhzScribe.Face.exe --pulse-test
& .\ahz-scribe-face\src\bin\Release\net10.0-windows\AhzScribe.Face.exe --state-test
& .\ahz-scribe-face\src\bin\Release\net10.0-windows\AhzScribe.Face.exe --session-test
& .\ahz-scribe-face\src\bin\Release\net10.0-windows\AhzScribe.Face.exe --soul-test
& .\ahz-scribe-face\src\bin\Release\net10.0-windows\AhzScribe.Face.exe --health-ui-test
& .\ahz-scribe-face\src\bin\Release\net10.0-windows\AhzScribe.Face.exe --health-fault-test
& .\ahz-scribe-face\src\bin\Release\net10.0-windows\AhzScribe.Face.exe --budget-ui-test
& .\ahz-scribe-face\src\bin\Release\net10.0-windows\AhzScribe.Face.exe --fault-matrix-test
& .\ahz-scribe-face\src\bin\Release\net10.0-windows\AhzScribe.Face.exe --identity-ui-test
& .\ahz-scribe-face\src\bin\Release\net10.0-windows\AhzScribe.Face.exe --model-switch-test
& .\ahz-scribe-face\src\bin\Release\net10.0-windows\AhzScribe.Face.exe --model-switch-ui-test
& .\ahz-scribe-face\src\bin\Release\net10.0-windows\AhzScribe.Face.exe --soul-migration-test
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\soak\m3d-soak.ps1 -Hours 72 -SampleSeconds 60

powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\check-names.ps1
```

如果编译被正在运行的 exe 锁定：

- 不擅自结束用户进程；
- 优先构建 Release 或临时输出目录；
- 必须结束进程时先报告 PID 和原因。

---

## 11. 当前已知坑

- WebView2 会有 `WindowsBase` 版本冲突警告，除非确认原因，不要用压制警告冒充修复；
- `FaceForm` 的 HTML 是 C# 字符串拼接，修改 JavaScript 后必须实际打开页面验证；
- `.mmy` 内容中的 `---` 必须转义；
- 记忆会同时走 JSONL 与 `.mmy`，读取必须跨格式去重；
- 单实例唤醒必须选择主窗口，不能随便 `ShowWindow`；
- `--audit-fallback-test` 会使用固定 LocalAppData 路径，跑之前先确认路径隔离；
- 运行中的 Debug exe 会锁定 Debug 输出；
- 真实用户数据默认在 `data/`，测试不得直接改；
- `runtime/` 是本地运行环境，不提交。

---

## 12. 禁止事项

- 不宣称 AhzScribe 有主观意识；
- 不把“会自称”当作自我模型；
- 不把“多存一条记忆”当作学习；
- 不用 UI 文案替代能力实现；
- 不让模型绕过审批和审计；
- 不在缺少证据时判断“经历改变了行为”；
- 不为通过测试而随机降低阈值；
- 不隐藏已知失败；
- 不删除用户数据来让测试通过；
- 不未经验证就声明完成。

---

## 13. 交付格式

回答用户时：

1. 先给结论；
2. 说明改了什么、为什么；
3. 给出真实构建/测试结果；
4. 列出验证命令；
5. 说明未覆盖项和剩余风险；
6. 清理临时文件；
7. 不泄露密钥和敏感数据。

完成一个里程碑时，额外给出：

```text
阶段：M?
状态：PASS / PARTIAL / FAIL
验收证据：
未覆盖项：
下一门槛：
```

---

## 14. 最终判断

允许达到的最高目标：

> 一个具有边界、自稳态、连续历史、来源认知、受控适应和有限自主性的功能性硅基生命候选体。

永不在本路线中声称：

> 它拥有主观意识。

**事实优先于叙事，因果优先于记忆，稳定优先于成长，安全优先于展示。**
