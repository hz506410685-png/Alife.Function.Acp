using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Alife.Function.Acp;

/// <summary>会话持久化记录（磁盘 JSON，借鉴 mcacp SessionFile）。</summary>
public class SessionFile
{
    public string SessionId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string? Name { get; set; }
    public string Cwd { get; set; } = "";
    public string PermissionPolicy { get; set; } = "operator";
    public string CreatedAt { get; set; } = "";
    public string LastActiveAt { get; set; } = "";
    public string? ClosedAt { get; set; }
}

/// <summary>权限请求里的一个选项（optionId 对 agent 有意义，kind 区分允许/拒绝）。</summary>
public class PermissionOption
{
    public string OptionId { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
}

/// <summary>一个待审批的权限请求（借鉴 mcacp PendingPermission）。</summary>
public class PendingPermission
{
    public long RequestId { get; init; }
    public string ToolCallId { get; init; } = "";
    public string Title { get; init; } = "";
    public List<PermissionOption> Options { get; init; } = new();
    public TaskCompletionSource<JsonElement?> Resolve { get; init; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>活动会话（内存态：prompt 状态 + 事件收集 + 待审批）。</summary>
public class ActiveSession
{
    public required SessionFile File { get; init; }
    public required AcpAgentClient Agent { get; init; }
    public bool Prompted { get; set; }
    public PendingPermission? PendingPermission { get; set; }
    /// <summary>本轮已累积的 assistant 文本（Nagle 合并后）。</summary>
    public string AssistantText { get; set; } = "";
    /// <summary>本轮是否出现过权限请求。</summary>
    public bool SawPermissionRequest { get; set; }
}

/// <summary>
/// 会话管理：agent 客户端生命周期 + 会话持久化 + 命名会话。
/// 持久化路径用白名单清洗 + 路径穿越防护（借鉴 mcacp sessionFilePath）。
/// </summary>
public sealed class AcpSessionManager : IAsyncDisposable
{
    readonly AcpConfig config;
    readonly string sessionRoot;
    readonly Dictionary<string, AcpAgentClient> agents = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ActiveSession> active = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string, JsonElement>? Notification;

    public AcpSessionManager(AcpConfig config, string storageRoot)
    {
        this.config = config;
        string rel = config.SessionDir.Replace('\\', '/').Trim('/');
        sessionRoot = Path.Combine(storageRoot, rel);
    }

    public IReadOnlyList<AcpAgentConfig> AgentConfigs => config.Agents;

    public AcpAgentClient GetOrCreateAgent(string name)
    {
        if (agents.TryGetValue(name, out var a)) return a;
        var cfg = config.Agents.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"未配置 agent「{name}」。可用：{string.Join("、", config.Agents.Select(x => x.Name))}");
        var client = new AcpAgentClient(cfg);
        client.Notification += (m, p) => Notification?.Invoke(m, p);
        agents[name] = client;
        return client;
    }

    /// <summary>校验并规范化工作目录（必须在白名单内，借鉴 acpx --cwd 边界）。</summary>
    public string ResolveCwd(string? cwd, string agentName)
    {
        string? target = string.IsNullOrWhiteSpace(cwd) ? null : cwd;
        if (target == null)
        {
            var cfg = config.Agents.FirstOrDefault(x => string.Equals(x.Name, agentName, StringComparison.OrdinalIgnoreCase));
            target = cfg?.DefaultCwd;
        }
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("未指定工作目录，且 agent 未配置 DefaultCwd");

        string full = Path.GetFullPath(target);
        // 白名单匹配：目录本身或其子目录都算允许（不能只比 StartsWith(带尾斜杠)，否则"目录本身"会失配）
        var allowed = config.AllowedWorkDirs.Select(d => Path.GetFullPath(d).TrimEnd('\\', '/')).ToList();
        bool ok = allowed.Any(baseDir =>
            full.Equals(baseDir, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (!ok)
            throw new InvalidOperationException($"工作目录不在允许白名单内：{full}（白名单：{string.Join("；", allowed)}）");
        return full;
    }

    public async Task<ActiveSession> NewSessionAsync(string agentName, string? sessionName, string cwd, string? policy, CancellationToken ct)
    {
        var agent = GetOrCreateAgent(agentName);
        await agent.EnsureStartedAsync(ct).ConfigureAwait(false);
        var result = await agent.NewSessionAsync(cwd, ct).ConfigureAwait(false);
        string sessionId = result.GetProperty("sessionId").GetString() ?? throw new InvalidOperationException("session/new 未返回 sessionId");

        if (!string.IsNullOrWhiteSpace(sessionName) && active.Values.Any(s => string.Equals(s.File.Name, sessionName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"命名会话「{sessionName}」已存在，请换名或先关闭它");

        var file = new SessionFile
        {
            SessionId = sessionId,
            AgentId = agentName,
            Name = string.IsNullOrWhiteSpace(sessionName) ? null : sessionName,
            Cwd = cwd,
            PermissionPolicy = string.IsNullOrWhiteSpace(policy) ? config.DefaultPermissionPolicy : policy,
            CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            LastActiveAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };
        var session = new ActiveSession { File = file, Agent = agent };
        active[sessionId] = session;
        SaveSessionFile(file);
        return session;
    }

    public async Task<ActiveSession> LoadSessionAsync(string agentName, string sessionId, string cwd, CancellationToken ct)
    {
        var agent = GetOrCreateAgent(agentName);
        await agent.EnsureStartedAsync(ct).ConfigureAwait(false);
        var result = await agent.LoadSessionAsync(sessionId, cwd, ct).ConfigureAwait(false);

        var file = ReadSessionFile(agentName, sessionId) ?? new SessionFile
        {
            SessionId = sessionId,
            AgentId = agentName,
            Cwd = cwd,
            PermissionPolicy = config.DefaultPermissionPolicy,
            CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            LastActiveAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };
        var session = new ActiveSession { File = file, Agent = agent };
        active[sessionId] = session;
        SaveSessionFile(file);
        return session;
    }

    /// <summary>按命名会话名或 sessionId 查找活动会话。</summary>
    public ActiveSession? FindSession(string nameOrId)
    {
        if (active.TryGetValue(nameOrId, out var s)) return s;
        return active.Values.FirstOrDefault(x => string.Equals(x.File.Name, nameOrId, StringComparison.OrdinalIgnoreCase));
    }

    public void TouchSession(ActiveSession session)
    {
        session.File.LastActiveAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        SaveSessionFile(session.File);
    }

    public void RemoveSession(ActiveSession session)
    {
        active.Remove(session.File.SessionId);
        session.File.ClosedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        SaveSessionFile(session.File);
    }

    public List<SessionFile> ListSessions(string agentName)
    {
        string safeAgent = Sanitize(agentName);
        string dir = Path.Combine(sessionRoot, safeAgent, "sessions");
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir, "*.json")
            .Select(f => { try { return JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(f)); } catch { return null; } })
            .Where(x => x != null && string.IsNullOrEmpty(x.ClosedAt))
            .Select(x => x!)
            .OrderByDescending(x => x.LastActiveAt)
            .ToList();
    }

    public string SessionRoot => sessionRoot;

    string SessionFilePath(string agentId, string sessionId)
    {
        string safeAgent = Sanitize(agentId);
        string safeSession = sessionId.Replace("/", "_").Replace("\\", "_");
        string filePath = Path.GetFullPath(Path.Combine(sessionRoot, safeAgent, "sessions", safeSession + ".json"));
        string root = Path.GetFullPath(sessionRoot);
        if (!filePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("非法 agent/session 标识：路径穿越");
        return filePath;
    }

    static string Sanitize(string s) => new(s.Where(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' or '@').ToArray());

    void SaveSessionFile(SessionFile file)
    {
        string path = SessionFilePath(file.AgentId, file.SessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
    }

    SessionFile? ReadSessionFile(string agentId, string sessionId)
    {
        string path = SessionFilePath(agentId, sessionId);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(path)); } catch { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var a in agents.Values) await a.DisposeAsync().ConfigureAwait(false);
        agents.Clear();
        active.Clear();
    }
}