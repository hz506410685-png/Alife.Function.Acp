using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Alife.Function.Acp;

/// <summary>
/// 单个 agent 的 ACP 客户端：懒启动 + initialize + session 生命周期方法。
/// 协议方法名与结构来自 ACP v1 官方规范及 codex-acp 实测。
/// </summary>
public sealed class AcpAgentClient : IAsyncDisposable
{
    readonly AcpAgentConfig config;
    readonly object startLock = new();
    readonly IReadOnlyDictionary<string, string>? configEnv;
    AcpConnection? conn;
    bool initialized;

    /// <summary>session/update 等通知，参数：(method, params)。</summary>
    public event Action<string, JsonElement>? Notification;

    /// <summary>agent 的 stderr 输出。</summary>
    public event Action<string>? ErrorOutput;

    /// <summary>agent 发来的权限请求（session/request_permission）。参数：(requestId, params)；返回决策 result；null=cancelled。</summary>
    public Func<long, JsonElement, Task<JsonElement?>>? PermissionRequestHandler;

    public string Name => config.Name;

    /// <summary>解析环境变量：支持 {auto:codex-auth} 占位符（从 ~/.codex/auth.json 读 OPENAI_API_KEY），避免明文存密钥。</summary>
    Dictionary<string, string> ResolveEnv()
    {
        var result = new Dictionary<string, string>();
        if (configEnv == null) return result;
        foreach (var kv in configEnv)
            result[kv.Key] = kv.Value == "{auto:codex-auth}" ? AutoCodexKey() : kv.Value;
        return result;
    }

    static string AutoCodexKey()
    {
        try
        {
            string authPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
            if (System.IO.File.Exists(authPath))
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(authPath));
                if (doc.RootElement.TryGetProperty("OPENAI_API_KEY", out var k) || doc.RootElement.TryGetProperty("CODEX_API_KEY", out k))
                    return k.GetString() ?? "";
            }
        }
        catch { }
        return "";
    }

    /// <summary>进程是否在运行（用于 agent_list 状态显示）。</summary>
    public bool IsRunningSafe()
    {
        try { return conn != null && conn.IsRunning; } catch { return false; }
    }

    public AcpAgentClient(AcpAgentConfig config)
    {
        this.config = config;
        configEnv = config.Env;
    }

    /// <summary>确保进程已启动并完成 initialize（懒启动）。</summary>
    public async Task EnsureStartedAsync(CancellationToken ct)
    {
        lock (startLock)
        {
            if (conn != null && conn.IsRunning) return;
            var env = ResolveEnv();
            conn = new AcpConnection(config.Command, config.Args, env, config.DefaultCwd);
            conn.Notification += (m, p) => Notification?.Invoke(m, p);
            conn.ErrorOutput += s => ErrorOutput?.Invoke(s);
            conn.InboundRequest += OnInboundRequestAsync;
            initialized = false;
        }
        if (initialized) return;

        var initParams = new Dictionary<string, object?>
        {
            ["protocolVersion"] = 1,
            ["clientCapabilities"] = new Dictionary<string, object?> { }
        };
        await conn.RequestAsync("initialize", initParams, ct).ConfigureAwait(false);
        initialized = true;
    }

    async Task<JsonElement?> OnInboundRequestAsync(long id, string method, JsonElement prms)
    {
        if (method == "session/request_permission" && PermissionRequestHandler != null)
            return await PermissionRequestHandler(id, prms).ConfigureAwait(false);
        // 未知入站请求一律取消，保证安全
        return null;
    }

    public async Task<JsonElement> NewSessionAsync(string cwd, CancellationToken ct)
    {
        return await conn!.RequestAsync("session/new", new Dictionary<string, object?>
        {
            ["cwd"] = cwd,
            ["mcpServers"] = Array.Empty<object>(),
        }, ct).ConfigureAwait(false);
    }

    public async Task<JsonElement> LoadSessionAsync(string sessionId, string cwd, CancellationToken ct)
    {
        return await conn!.RequestAsync("session/load", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["cwd"] = cwd,
            ["mcpServers"] = Array.Empty<object>(),
        }, ct).ConfigureAwait(false);
    }

    /// <summary>发送 prompt 并等待 turn 结束（响应含 stopReason）。事件经 Notification 流入 PromptHandler。</summary>
    public async Task<JsonElement> PromptAsync(string sessionId, List<object> promptBlocks, CancellationToken ct)
    {
        return await conn!.RequestAsync("session/prompt", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["prompt"] = promptBlocks,
        }, ct).ConfigureAwait(false);
    }

    public async Task CancelAsync(string sessionId, CancellationToken ct)
    {
        await conn!.RequestAsync("session/cancel", new Dictionary<string, object?> { ["sessionId"] = sessionId }, ct).ConfigureAwait(false);
    }

    public async Task CloseAsync(string sessionId, CancellationToken ct)
    {
        await conn!.RequestAsync("session/close", new Dictionary<string, object?> { ["sessionId"] = sessionId }, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (conn != null) await conn.DisposeAsync().ConfigureAwait(false);
        conn = null;
    }
}
