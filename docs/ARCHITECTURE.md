# 架构说明（Architecture）

## 总体设计

Alife.Function.Acp 是一个原生 C# Alife 插件：露露（Alife 陪伴智能体）通过 XML 函数发出指挥指令，插件将指令翻译成 **Agent Client Protocol (ACP)** 调用，指挥本机一个 ACP agent 进程（默认 Codex via codex-acp）干活，并把结果 Poke 回对话。

```
露露
  │  <agent_list/> <agent_start/> <派活>…</派活> <催活/> <审批/> <收回/> <会话列表/>
  ▼
AcpService ── XmlHandler（7 个函数）
  │
  ├─ AcpSessionManager   agent 生命周期 + 会话持久化 + 命名会话 + 目录白名单
  ├─ AcpPromptHandler    prompt 编排（同步等待 / Nagle 合并 / 超时 / 权限中断）
  ├─ AcpPermissionEngine 权限策略自动决策 + 审批应答
  ├─ AcpMessageLayer     Ask / Briefing 模板 + 脱敏
  └─ AcpAgentClient ── AcpConnection（JSON-RPC 2.0 over stdio）
                          │
                          ▼
                     ACP agent（codex-acp → Codex）
```

## 模块职责

### AcpService.cs
- 插件主类，继承 `ChatBehaviour`，实现 `IConfigurable<AcpConfig>`
- `OnAwake`：初始化 SessionManager / PromptHandler，注册 `XmlHandler`，把 7 个 `[XmlFunction]` 方法暴露给露露
- 所有函数结果通过 `interactor.Poke(text)` 推回对话（Alife 函数没有 return 通道）

### AcpAgentClient.cs
- 每个配置的 agent 一个客户端，**懒启动**：首次使用时才 spawn 进程并 `initialize`
- 环境变量解析：支持 `{auto:codex-auth}` 占位符（从 `~/.codex/auth.json` 读 key）
- 封装 `session/new`、`session/load`、`session/prompt`、`session/cancel`、`session/close`
- 入站请求只处理 `session/request_permission`，其余一律取消（安全兜底）

### AcpConnection.cs
- JSON-RPC 2.0 over stdio（NDJSON）传输层
- 进程管理照抄 Alife `PythonService` 的子进程模式：`UseShellExecute=false`、stdout/stderr 重定向、UTF-8、退出清理
- 请求按 id 匹配 `TaskCompletionSource`；notification / 入站 request 通过事件向上抛

### AcpSessionManager.cs
- 会话持久化：磁盘 JSON（SessionFile），字段含 `sessionId / agentId / name / cwd / permissionPolicy / createdAt / lastActiveAt / closedAt`
- 命名会话：`派活 会话名=backend` 会复用同名会话继续干
- 安全：agentId/sessionId 写入前白名单清洗 + 路径穿越校验；`ResolveCwd` 强制命中 `AllowedWorkDirs`

### AcpPermissionEngine.cs
- 策略自动决策：`allow_all` / `allow_readonly`（根据 toolCall title 判读写）/ `deny_all`
- `operator` 策略：挂起等待露露 `审批`
- 决策统一映射为 agent 的 `selected.optionId` 或 `cancelled`

### AcpPromptHandler.cs
- 发送 prompt 后阻塞收集事件，直到完成 / 权限请求 / 超时
- 事件过滤：只消费 `session/update` 中的 `agent_message_chunk` / `agent_thought_chunk`
- Nagle 式合并：`ConsolidateMs` 窗口内合并分片，遇换行立即刷新
- 权限中断 / 超时提前返回后，后台最终结果通过 `PromptFinished` 事件主动汇报

### AcpMessageLayer.cs
- `BuildAsk`：单次任务消息（`--- ACP START ---` 包裹的 Ask 模板）
- `BuildBriefing`：项目交接整包上下文（Purpose / Project / Architecture / Known Issues / Key Decisions）
- `Redact`：替换 `sk-*`、Bearer token、Windows 绝对路径、`key=value` 密钥

## 配置模型（AcpConfig.cs）

| 字段 | 默认 | 说明 |
|---|---|---|
| `Agents` | `[]` | agent 配置列表（Name/Command/Args/Env/DefaultCwd） |
| `AllowedWorkDirs` | `[]` | 工作目录白名单（安全边界） |
| `DefaultPermissionPolicy` | `operator` | 默认权限策略 |
| `PromptTimeoutSec` | `300` | 派活超时（秒） |
| `ConsolidateMs` | `300` | 消息分片合并窗口（毫秒） |
| `RedactSecrets` | `true` | 发送前脱敏 |
| `SessionDir` | `Data/Acp/sessions` | 会话持久化目录（相对 Storage） |

## 设计借鉴

| 来源 | 借鉴点 |
|---|---|
| mcacp | 会话管理、PermissionEngine、PromptHandler（Nagle 合并、事件过滤）、路径安全 |
| acpx | 命名会话、`--cwd` 文件系统边界、prompt 排队 |
| codex-acp | agent 侧能力协商、事件类型全貌、stdio server 形态 |
| agent-client-protocol | ACP v1 协议核心方法 |
| dingshuxin/acp | Ask/Briefing 消息模板、脱敏、上下文裁剪 |
| Alife 自身先例 | PythonService 子进程模式、McpService 的 ChatBehaviour + XmlHandler + Poke |
