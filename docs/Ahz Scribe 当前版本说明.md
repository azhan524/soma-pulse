# Ahz Scribe 当前版本说明

版本：`v0.1.0-alpha.16`

这不是一份愿景文案，而是对当前代码实际行为的说明。

---

## 一、日常打开时看到什么

### 托盘

托盘里有一个圆点，表示进程活着。

| 颜色 | 含义 |
|---|---|
| 灰色 | 睡着 / 未激活 |
| 蓝色 | 活着，慢呼吸 |
| 黄色 | 有事等你处理 |
| 红色 | 急停 |

托盘菜单：

```text
打开主窗口
暂停主动消息
急停
退出
```

关闭主窗口默认进入托盘，不退出进程。

### 主窗口

顶栏：

```text
状态点
空闲时长
前台程序
磁盘剩余
当前时间
```

左栏五张状态卡：

```text
当前焦点
未完成意图
身体摘要
最近脉冲
情绪
```

六个 Tab：

```text
对话 / 脉冲 / 记忆 / 审计 / 任务 / 设置
```

### 对话

支持：

```text
普通对话
发送队列
最后响应时间
等待 / 错误 / 最终状态
```

当前没有：

```text
流式输出
工具调用折叠在消息气泡下方
真正的消息气泡布局
```

### 脉冲

每次醒来一行：

```text
时间
触发原因：timer / intent_due / body_event / user / drive
动作：none / message / memory / tool
原因
```

`none` 和 `message` 一样可以展开。

展开内容：

```text
state_restore
thought
action / reason
state_writeback
confidence
pulse_id
tool_calls
memories
```

其中 `tool_calls` 在脉冲路径为空，因为脉冲决策器本身不调用工具；工具调用发生在 AgentRunner 路径。

### 记忆

```text
语义记忆：表格
情节记忆：卡片 / 时间线
冲突记忆：bg-disputed #3A3520
```

当前不是每条记忆都完整展示置信度和 `source_audit_id`。

### 审计

按时间排列：

```text
时间
span
tool
decision
result
reason
```

被拦下的操作有红线。

备用通道记录带 `[备用]`。

设置 → 权限中可以打开：

```text
face 审计
备用通道
主审计
```

### 任务

显示：

```text
active_task
开始时间
任务步骤
未完成意图
Marquee 进度条
```

任务是步骤级展示，不是真正的多步执行器。

### 设置

分组：

```text
LLM
身体
安全
权限
记忆
脉冲
主动性
人格
```

权限支持：

```text
只读模式：禁止 WorkspaceWrite / Full 工具
```

记忆支持：

```text
检索条数 1–50
```

---

## 二、不看它的时候，它在后台做什么

每 30 秒采集身体状态：

```text
前台窗口
空闲时长
CPU / 内存 / 磁盘
网络
电池
```

写入：

```text
_face-data/body_state.json
```

定时检查：

```text
open intents 是否到期
```

到期不等于一定找用户。脉冲会判断：

```text
现在合适吗
要不要开口
还是 none
```

主动消息限制：

```text
每日上限
静默时段
连续未回复暂停
```

记忆提取：

```text
一轮对话后提取 0–3 条
寒暄、临时状态、猜测不写
```

内驱引擎：

```text
每天最多生成一个“想探究”的目标
进入 open_intents
不直接找用户
```

---

## 三、坏的时候怎么表现

### 急停

立即停止，不弹确认框。

当前表现：

```text
托盘红点
聊天区提示“已急停，所有脉冲停止”
/resume 恢复
```

当前没有：

```text
所有 Tab 顶部黄色“已急停”横幅
底栏急停按钮变成“恢复”
恢复二次确认
```

### WebView2 崩溃

脉冲 / 记忆 / 审计显示失败面板：

```text
错误原因
失败次数
重试按钮
```

连续三次失败：

```text
永久只读
显示最近脉冲 / 记忆 / 审计快照
重试禁用
```

主程序不退出，对话和任务照常。

### 审计目录不可写

单条记录写失败时：

```text
写 %LOCALAPPDATA%\Ahz\audit_fallback\audit_fallback.jsonl
```

当前不是全局只读，而是单条降级。

`MergeFallback` 还没有按 `audit_id` 去重。

### 传感器

心跳超过 90 秒：

```text
RestartBodySensor()
```

当前不是 30 秒，也没有连续三次失败顶栏标红。

### 配置错误

`settings.json` 的 `SchemaVersion` 不匹配：

```text
拒绝启动
```

### LLM 连不上

请求失败会显示错误。

当前顶栏没有独立的“大脑离线”黄色状态。

---

## 四、它明确不做什么

```text
不是永远在线聊天机器人
不会因为用户没理它就反复找用户
不会隐藏它没做的事
none 和 message 一样可见
不会把“想到”当成“做到”
```

最后一条目前部分成立：

```text
thought / reason 有单独标签
事实层和解释层还没有完全不同的字体 / 底色体系
```

---

## 五、当前已知差距

按优先级：

```text
1. 急停没有全局横幅 / 恢复按钮 / 二次确认
2. 审计不是全局只读，合并没有按 audit_id 去重
3. 传感器是 90 秒，不是 30 秒 + 三次失败标红
4. LLM 离线没有独立顶栏状态
5. 对话没有流式输出和工具气泡
6. 记忆置信度 / 来源没有逐条完整展示
```

---

## 六、本地数据位置

Face 数据：

```text
ahz-scribe-face/src/bin/Debug/net10.0-windows/_face-data/
```

主要文件：

```text
sessions.jsonl
pulse.jsonl
memory.jsonl
semantic.jsonl
episodic.jsonl
audit.jsonl
body_state.json
settings.json
```

备用审计：

```text
%LOCALAPPDATA%\Ahz\audit_fallback\audit_fallback.jsonl
```

主审计：

```text
C:\ProgramData\AhzScribe\audit\audit.jsonl
```

---

## 七、验证状态

当前已通过：

```text
contracts / brain / body / face 构建
BODY SELF-TEST
audit fallback
WebView2 process failure
sensor stale restart
schema mismatch reject
六 Tab UI Automation
```

自动测试入口：

```powershell
AhzScribe.Face.exe --audit-fallback-test
AhzScribe.Face.exe --sensor-stale-test
AhzScribe.Face.exe --schema-test
```

---

## 一句话

> Ahz Scribe 现在是一个能持续运行、能被观察、能被倒查的本地 Agent 观察窗。
>
> 它还不是“每一句愿景都已经完全成立”的最终形态，但主链路已经可用，剩余差距集中在急停 UI、审计原子性、传感器故障计数、流式对话和记忆溯源展示。
