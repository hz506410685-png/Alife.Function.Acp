# Alife.Function.Acp

让 [Alife](https://github.com/BDFFZI/Alife)（露露）通过 **Agent Client Protocol (ACP)** 指挥本机编码 agent（如 Codex、Claude 等），成为你的「项目经理」。

露露用一组高语义函数下发任务、跟踪进度、审批权限、收回任务，会话持久化到磁盘，重启 Alife 后可以继续。

> 原生 C# 插件，基于 Alife 官方 `ChatBehaviour` + `XmlFunction` 框架，不依赖任何第三方运行时（除被指挥的 ACP agent 本身）。

---

## 功能特性

- **派活 / 催活 / 审批 / 收回**：露露视角的项目管理指挥语言
  - `派活`：新建或复用**命名会话**，发送任务简报并等待结果
  - `催活`：任务超时后取回进度 / 继续等待
  - `审批`：agent 请求权限时，露露决定同意或拒绝
  - `收回`：取消并关闭会话（停止任务）
- **固定会话窗口**：不指定会话名时默认复用同一会话（配置 `DefaultSessionName`），不再每次开新窗口；持久化会话跨重启恢复上下文
- **多 agent 配置**：同一插件可配置多个 ACP agent（默认 Codex via `codex-acp`）
- **安全设计**
  - 工作目录**白名单**（`AllowedWorkDirs`），目录穿越防护
  - 权限策略：`operator`（露露审批）/ `allow_readonly` / `allow_all` / `deny_all`
  - API key 通过 `{auto:codex-auth}` 占位符自动读取，**不落明文密钥**
  - 发送给 agent 的内容自动**脱敏**（API key / Bearer token / 本地路径）
- **消息层**：借鉴 [dingshuxin353/acp](https://github.com/dingshuxin353/acp) 的 `Ask / Briefing` 结构化消息模板，降低 agent 误解、可裁剪、可追踪

---

## 工作原理

```
露露（Alife 对话）
   │  <agent_list/> <agent_start/> <派活>…</派活> <审批/> <催活/> <收回/> <会话列表/>
   ▼
AcpService（ChatBehaviour 主类）
   │
   ├── AcpSessionManager  会话持久化 / 命名会话 / 目录白名单 / agent 生命周期
   ├── AcpPromptHandler   prompt 编排：事件收集、Nagle 分片合并、超时、权限中断
   ├── AcpPermissionEngine 权限策略自动决策 + 审批应答
   ├── AcpMessageLayer    Ask / Briefing 消息模板 + 脱敏
   └── AcpAgentClient ── AcpConnection（JSON-RPC 2.0 over stdio）
                            │
                            ▼
                       ACP agent 进程（如 codex-acp → Codex）
```

| 文件 | 职责 |
|---|---|
| `AcpService.cs` | 插件主类：注册 7 个 XML 函数、生命周期、结果 Poke 回对话 |
| `AcpAgentClient.cs` | ACP 客户端：懒启动、initialize、session/new、load、prompt、cancel、close |
| `AcpConnection.cs` | JSON-RPC 2.0 over stdio（NDJSON）传输层，子进程管理 |
| `AcpSessionManager.cs` | 会话持久化（磁盘 JSON）、命名会话、工作目录白名单与路径穿越防护 |
| `AcpPermissionEngine.cs` | 权限策略：`operator` / `allow_readonly` / `allow_all` / `deny_all` |
| `AcpPromptHandler.cs` | prompt 编排：同步等待、事件过滤、Nagle 合并、超时、权限中断 |
| `AcpMessageLayer.cs` | dingshuxin 风格 `Ask / Briefing` 消息模板 + 脱敏 |
| `AcpConfig.cs` | 配置模型（`IConfigurable`） |

---

## 快速开始

### 1. 安装插件

把本仓库的 `Alife.Function.Acp/` 整个目录复制到 Alife 的插件目录：

```powershell
# 假设 Alife 安装在 C:\Users\<you>\Documents\Alife
Copy-Item -Recurse .\Alife.Function.Acp C:\Users\<you>\Documents\Alife\Storage\Plugins\
```

> 提示：若 `Runtime\PluginContext\CompiledPlugins` 下有旧的 `Alife.Function.Acp.dll`，删除后重启 Alife 会强制重新编译。

### 2. 准备 ACP agent（以 Codex 为例）

用 Alife 自带（或系统）的 node 安装 `codex-acp`：

```powershell
$nodeDir = "C:\Users\<you>\Documents\Alife\Storage\node-v22.14.0-win-x64"
$toolDir = "C:\Users\<you>\Documents\Alife\Storage\Tools\codex-acp"
& "$nodeDir\npm.cmd" install @agentclientprotocol/codex-acp --prefix $toolDir --no-audit --no-fund
```

### 3. 配置

插件配置由 Alife 配置系统自动生成（`Storage\Configuration\Alife.Function.Acp.AcpService.json`）：

```jsonc
{
  "Agents": [
    {
      "Name": "codex",
      "Command": "C:\\Users\\<you>\\Documents\\Alife\\Storage\\node-v22.14.0-win-x64\\node.exe",
      "Args": [
        "C:\\Users\\<you>\\Documents\\Alife\\Storage\\Tools\\codex-acp\\node_modules\\@agentclientprotocol\\codex-acp\\dist\\index.js"
      ],
      "Env": {
        "OPENAI_API_KEY": "{auto:codex-auth}"
      },
      "DefaultCwd": "C:\\Users\\<you>\\Documents\\Codex"
    }
  ],
  "AllowedWorkDirs": [
    "C:\\Users\\<you>\\Documents\\Codex",
    "C:\\Users\\<you>\\Documents\\Alife"
  ],
  "DefaultPermissionPolicy": "operator",
  "PromptTimeoutSec": 300,
  "ConsolidateMs": 300,
  "RedactSecrets": true,
  "SessionDir": "Data/Acp/sessions"
}
```

配置说明：

| 字段 | 说明 |
|---|---|
| `Agents[].Name` | agent 名字，露露用它引用（如 `codex`） |
| `Agents[].Command/Args` | ACP agent 启动命令（推荐 Alife 自带 node.exe + codex-acp `dist/index.js`） |
| `Agents[].Env` | 附加环境变量；`{auto:codex-auth}` 自动从 `~/.codex/auth.json` 读 `OPENAI_API_KEY` |
| `AllowedWorkDirs` | 工作目录白名单（安全边界），agent 只能在这里干活 |
| `DefaultPermissionPolicy` | `operator`（露露审批）/ `allow_readonly`（只读放行）/ `allow_all` |
| `PromptTimeoutSec` | 派活等待超时（秒），超时后可用 `催活` 取回 |
| `ConsolidateMs` | agent 消息分片合并窗口（毫秒，Nagle 式） |
| `RedactSecrets` | 发送前是否脱敏 |
| `SessionDir` | 会话持久化目录（相对 Alife Storage） |

---

## 使用示例（露露侧）

```
<agent_list/>
<agent_start agent="codex" cwd="C:\dev\myproj"/>
```

派活（内容用 ACP 简报格式，或直接写清任务 / 上下文 / 验收标准）：

```
<派活 agent="codex" 会话名="backend" 权限="operator" 验收标准="...">用一句话介绍你自己，不要动任何文件</派活>
```

```
<催活 会话名="backend"/>
<审批 会话名="backend" 决定="同意" 理由="可信操作"/>
<收回 会话名="backend"/>
<会话列表/>
```

| 函数 | 模式 | 说明 |
|---|---|---|
| `agent_list` | OneShot | 列出已配置 agent 及其运行状态 |
| `agent_start` | OneShot | 启动并初始化 agent（懒启动，派活时也会自动启动） |
| `派活` | Content | 复用固定/命名会话（同一窗口）+ 发送任务 + 等待结果 |
| `催活` | OneShot | 取回超时任务的进度 / 继续等待 |
| `审批` | OneShot | 处理权限请求（同意 / 拒绝） |
| `收回` | OneShot | 取消并关闭会话 |
| `会话列表` | OneShot | 列出所有未关闭会话 |

---

## 安全设计

1. **目录白名单**：`ResolveCwd` 校验会话工作目录必须命中 `AllowedWorkDirs`（目录本身或其子目录），借鉴 acpx 的 `--cwd` 边界。
2. **路径穿越防护**：会话持久化路径使用 agentId/sessionId 白名单清洗 + `Path.GetFullPath` 归一化校验，防止 `../` 逃逸。
3. **密钥不入盘**：`OPENAI_API_KEY: {auto:codex-auth}` 在运行时从 `~/.codex/auth.json` 读取，配置文件与磁盘不落明文密钥。
4. **脱敏**：发往 agent 的内容会替换 `sk-*` 密钥、`Bearer token`、Windows 绝对路径等敏感信息（可关）。
5. **权限策略**：默认 `operator`——agent 想写文件 / 执行命令时挂起等露露审批；未知入站请求一律取消。

---

## 借鉴与致谢

本插件是以下优秀开源项目在 Alife 框架下的落地：

- [Oortonaut/mcacp](https://github.com/Oortonaut/mcacp) — MCP→ACP 桥：会话管理、权限引擎、PromptHandler（同步/轮询、Nagle 合并、事件过滤）、路径安全
- [openclaw/acpx](https://github.com/openclaw/acpx) — 无头 ACP CLI：命名会话、`--cwd` 文件系统边界、prompt 排队
- [agentclientprotocol/codex-acp](https://github.com/agentclientprotocol/codex-acp) — Codex 的 ACP agent 适配器（本插件默认 agent）
- [agentclientprotocol/agent-client-protocol](https://github.com/agentclientprotocol/agent-client-protocol) — 官方 ACP 协议
- [dingshuxin353/acp](https://github.com/dingshuxin353/acp) — Agent 间消息内容格式（Ask/Briefing、上下文裁剪、脱敏）

---

## 已知限制 / Roadmap

- [ ] 多 agent 并行编排
- [ ] client 侧 fs / terminal handler（让 agent 读露露的记忆文件）
- [ ] WebSocket / HTTP 远程 agent
- [ ] plan 里程碑事件单独上报

---

## 许可证

[MIT](./LICENSE)
