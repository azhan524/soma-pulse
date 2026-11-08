# SomaPulse naming

## 规则

1. 项目品牌为 **SomaPulse / 脉体计划**，公开仓库为 `soma-pulse`。
2. Agent 名为 **AhzScribe**；项目名和 Agent 名不能混用。
3. `ahz-scribe-*` 与 `AhzScribe.*` 是已验证版本的兼容标识，当前不随品牌改名。
4. 只有在一次明确的破坏性版本升级中，才能统一迁移程序集和模块目录名称。
5. 目录、服务、容器和镜像名继续使用小写 kebab-case，并保持三者一致。
6. 当前稳定模块角色只有三个：

```text
ahz-scribe-brain
ahz-scribe-body
ahz-scribe-face
```

## 唯一事实来源

| 角色 | 目录 | Git 子模块 | Docker 服务 | 容器名 | 镜像 |
|---|---|---|---|---|---|
| 脑 | `ahz-scribe-brain` | `ahz-scribe-brain` | `ahz-scribe-brain` | `ahz-scribe-brain` | `ghcr.io/azhan524/ahz-scribe-brain:latest` |
| 身 | `ahz-scribe-body` | `ahz-scribe-body` | `ahz-scribe-body` | `ahz-scribe-body` | `ghcr.io/azhan524/ahz-scribe-body:latest` |
| 脸 | `ahz-scribe-face` | `ahz-scribe-face` | `ahz-scribe-face` | `ahz-scribe-face` | `ghcr.io/azhan524/ahz-scribe-face:latest` |

## 能力边界

```text
brain: 模型 API；不挂工作区；无 shell；只发 action-request
body:  工作区；工具；审批；审计；快照；系统调用
face:  只连 brain/body 的本地接口；不挂工作区；不直接调工具
```

## 禁止

```text
ahz_scribe
ahz-scribe-Brain
brain
face
body
ahz-scribe-brain-service
soma_pulse
Soma-Pulse
```

说明：`AhzScribe` 是正式 Agent 名称，不再列为禁止词。

## 校验

`tools/check-names.ps1` 会检查目录、compose 服务名/容器名、`.gitmodules` 路径。
