using System.Collections.Generic;

namespace Alife.Function.Acp;

/// <summary>单个 ACP agent（编码 agent）的启动配置。</summary>
public class AcpAgentConfig
{
    /// <summary>agent 名字，露露用这个名字引用它（如 codex）。</summary>
    public string Name { get; set; } = "codex";

    /// <summary>启动命令（可执行文件路径）。推荐 Alife 自带 node.exe。</summary>
    public string Command { get; set; } = "";

    /// <summary>命令参数（如 codex-acp 的 dist/index.js 路径）。</summary>
    public string[] Args { get; set; } = [];

    /// <summary>附加环境变量（如 OPENAI_API_KEY / CODEX_CONFIG）。</summary>
    public Dictionary<string, string> Env { get; set; } = new();

    /// <summary>默认工作目录（会话 cwd，必须在 AllowedWorkDirs 内）。</summary>
    public string DefaultCwd { get; set; } = "";
}

/// <summary>插件全局配置（IConfigurable）。存于 Alife 配置系统，不碰 MCP 配置。</summary>
public class AcpConfig
{
    /// <summary>可用 agent 列表。</summary>
    public List<AcpAgentConfig> Agents { get; set; } = new();

    /// <summary>允许的工作目录白名单（安全边界，借鉴 acpx --cwd）。</summary>
    public List<string> AllowedWorkDirs { get; set; } = new();

    /// <summary>默认权限策略：operator（露露审批）/ allow_readonly（只读放行）/ allow_all（全放行）。</summary>
    public string DefaultPermissionPolicy { get; set; } = "operator";

    /// <summary>不指定会话名时使用的固定会话名（默认 "default"，同一窗口复用；留空则每次新建会话）。</summary>
    public string DefaultSessionName { get; set; } = "default";

    /// <summary>派活等待超时（秒）。</summary>
    public int PromptTimeoutSec { get; set; } = 300;

    /// <summary>agent 消息分片合并窗口（毫秒，Nagle 式，借鉴 mcacp）。</summary>
    public int ConsolidateMs { get; set; } = 300;

    /// <summary>发送给 agent 前是否脱敏（密钥/路径/隐私）。</summary>
    public bool RedactSecrets { get; set; } = true;

    /// <summary>会话持久化目录（相对 Storage，如 Data/Acp/sessions）。</summary>
    public string SessionDir { get; set; } = "Data/Acp/sessions";
}