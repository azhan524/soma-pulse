# ahz-scribe-body

只负责“做”。

- 工具注册表与参数校验；
- 权限档、审批、工作区外拦截；
- 文件 / shell / 进程 / 网络 / 桌面操作；
- 快照、回滚；
- append-only 审计；
- 身体感采样（body-state）；
- 向 brain / face 返回结构化结果。

不直接：

- 调大模型；
- 自己决定“要不要做”——只能按策略校验 brain 的请求；
- 自己批准自己。

迁移来源（临时）：`../local-agent/src/AhzScribe.cs` 中的
`Tools` / `Execute` / `SnapStore` / `Audit` / `BodySensor`。

## v0（已可构建）

```text
src/AhzScribe.Body.csproj
src/AhzScribe.Body.cs
```

已实现：

- `IActionExecutor`
- named pipe `ahz-scribe-body`
- `read_file` / `list_dir` / `write_file` / `file_info` / `make_dir` / `delete_file` / `copy_file` / `move_file`
- `run_shell` / `start_program` / `network_fetch` / `desktop_click` / `desktop_type` / `desktop_focus_window` / `desktop_click_window`（仅 Full + Approved=true）
- `search_text` / `tail_file` / `glob` / `desktop_list_windows` / `desktop_active_window` / `desktop_screenshot` / `rollback_self`（只读）
- workspace 越界拦截
- 只读档拒绝写入
- JSONL 审计（v0 本地文件）
- `--self-test` / `--ipc-test`

构建：

```bat
dotnet build src\AhzScribe.Body.csproj
```

自测：

```bat
dotnet run --project src\AhzScribe.Body.csproj -- --self-test
```

v0 不包含：shell、进程、网络、桌面、快照、审批 UI。
这些仍由旧单文件栈提供，后续按迁移计划搬入。

```text
container: ahz-scribe-body
service:   ahz-scribe-body
```
