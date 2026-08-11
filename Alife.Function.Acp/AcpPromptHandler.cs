using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Alife.Function.Acp;

/// <summary>一次 prompt 的结果。</summary>
public class PromptOutcome
{
    /// <summary>agent 最终回复文本（合并后）。</summary>
    public string Text { get; set; } = "";
    public string StopReason { get; set; } = "";
    /// <summary>是否超时返回（任务仍在后台进行）。</summary>
    public bool TimedOut { get; set; }
    /// <summary>若为 true，agent 正在等权限审批（露露需调用审批函数）。</summary>
    public bool NeedsApproval { get; set; }
    public string Error { get; set; } = "";
}

/// <summary>
/// prompt 编排：发送 prompt，阻塞收集事件直到完成/权限请求/超时。
/// 事件过滤（只留消息文本）、Nagle 式分片合并 —— 借鉴 mcacp PromptHandler。
/// </summary>
public sealed class AcpPromptHandler
{
    readonly AcpSessionManager sessions;
    readonly AcpConfig config;
    readonly object gate = new();
    readonly Dictionary<string, PromptCollector> collectors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>prompt 已返回但 agent 仍在后台跑（权限中断/超时后最终完成）时触发，用于主动汇报。</summary>
    public event Action<ActiveSession, PromptOutcome>? PromptFinished;

    public AcpPromptHandler(AcpSessionManager sessions, AcpConfig config)
    {
        this.sessions = sessions;
        this.config = config;
        sessions.Notification += HandleNotification;
    }

    class PromptCollector
    {
        public required string SessionId { get; init; }
        public required TaskCompletionSource<PromptOutcome> Tcs { get; init; }
        public StringBuilder Buffer { get; } = new();
        public System.Threading.Timer? ChunkTimer;
        public bool Done;
    }

    /// <summary>发送 prompt 并等待结果（同步语义）。ct 取消会中断等待并尝试 cancel。</summary>
    public async Task<PromptOutcome> PromptAsync(ActiveSession session, string promptText, CancellationToken ct)
    {
        if (session.Prompted)
            throw new InvalidOperationException($"会话「{session.File.Name ?? session.File.SessionId}」正在执行任务，请用催活或另开会话");

        session.Prompted = true;
        session.AssistantText = "";
        session.SawPermissionRequest = false;
        var tcs = new TaskCompletionSource<PromptOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var collector = new PromptCollector { SessionId = session.File.SessionId, Tcs = tcs };
        lock (gate) collectors[session.File.SessionId] = collector;

        var blocks = new List<object> { new Dictionary<string, object?> { ["type"] = "text", ["text"] = promptText } };
        int timeoutMs = Math.Max(10, config.PromptTimeoutSec * 1000);
        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        Task<JsonElement> promptTask;
        try
        {
            promptTask = session.Agent.PromptAsync(session.File.SessionId, blocks, linked.Token);
        }
        catch (Exception ex)
        {
            Cleanup(session.File.SessionId);
            session.Prompted = false;
            return new PromptOutcome { Error = ex.Message };
        }

        _ = ObservePromptResponseAsync(session, promptTask);

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
            {
                try { await session.Agent.CancelAsync(session.File.SessionId, CancellationToken.None).ConfigureAwait(false); } catch { }
                return new PromptOutcome { Error = "任务已被取消" };
            }
            return new PromptOutcome { Text = session.AssistantText, TimedOut = true };
        }
        finally
        {
            Cleanup(session.File.SessionId);
            session.Prompted = false;
        }
    }

    async Task ObservePromptResponseAsync(ActiveSession session, Task<JsonElement> promptTask)
    {
        PromptOutcome outcome;
        try
        {
            var res = await promptTask.ConfigureAwait(false);
            outcome = res.TryGetProperty("stopReason", out var sr)
                ? new PromptOutcome { Text = session.AssistantText, StopReason = sr.GetString() ?? "" }
                : new PromptOutcome { Text = session.AssistantText, Error = "agent 未返回 stopReason" };
        }
        catch (Exception ex)
        {
            outcome = new PromptOutcome { Text = session.AssistantText, Error = ex.Message };
        }

        bool delivered = TryComplete(session.File.SessionId, outcome);
        // 若 prompt 已被权限/超时提前返回，则这是后台晚到的最终结果 → 主动汇报
        if (!delivered && (outcome.Error.Length > 0 || outcome.Text.Length > 0))
            PromptFinished?.Invoke(session, outcome);
    }

    /// <summary>处理 agent 的 session/request_permission：按策略自动决策，否则挂起等露露审批。</summary>
    public async Task<JsonElement?> OnPermissionRequestAsync(ActiveSession session, long requestId, JsonElement prms)
    {
        session.SawPermissionRequest = true;

        string title = prms.TryGetProperty("toolCall", out var tc) && tc.TryGetProperty("title", out var t)
            ? t.GetString() ?? ""
            : prms.TryGetProperty("title", out var t2) ? t2.GetString() ?? "" : "";
        string toolCallId = prms.TryGetProperty("toolCall", out var tc2) && tc2.TryGetProperty("toolCallId", out var tcId)
            ? tcId.GetString() ?? ""
            : prms.TryGetProperty("toolCallId", out var t3) ? t3.GetString() ?? "" : "";

        var options = new List<PermissionOption>();
        if (prms.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
        {
            foreach (var o in opts.EnumerateArray())
            {
                options.Add(new PermissionOption
                {
                    OptionId = o.TryGetProperty("optionId", out var idEl) ? idEl.GetString() ?? "" : "",
                    Kind = o.TryGetProperty("kind", out var kEl) ? kEl.GetString() ?? "" : "",
                    Name = o.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "",
                });
            }
        }

        // 策略自动决策（allow_all / allow_readonly / deny_all）
        var auto = AcpPermissionEngine.AutoDecide(session.File.PermissionPolicy, prms);
        if (auto != null) return auto;

        // operator：挂起等露露审批
        var pending = new PendingPermission
        {
            RequestId = requestId,
            ToolCallId = toolCallId,
            Title = string.IsNullOrEmpty(title) ? "(未命名操作)" : title,
            Options = options,
        };
        session.PendingPermission = pending;

        TryComplete(session.File.SessionId, new PromptOutcome
        {
            Text = session.AssistantText,
            NeedsApproval = true,
        });

        return await pending.Resolve.Task.ConfigureAwait(false);
    }

    /// <summary>露露审批。allow=true 同意，false 拒绝。返回是否成功处理。</summary>
    public bool ResolvePermission(ActiveSession session, bool allow)
    {
        var pending = session.PendingPermission;
        if (pending == null) return false;

        JsonElement? result = AcpPermissionEngine.BuildDecision(pending, allow);
        pending.Resolve.TrySetResult(result);
        session.PendingPermission = null;
        return true;
    }

    void HandleNotification(string method, JsonElement prms)
    {
        if (method != "session/update") return;
        if (!prms.TryGetProperty("sessionId", out var sidEl)) return;
        string sessionId = sidEl.GetString() ?? "";
        if (!prms.TryGetProperty("update", out var update) || update.TryGetProperty("sessionUpdate", out var su) == false) return;
        string updateType = su.GetString() ?? "";

        if (updateType is "agent_message_chunk" or "agent_thought_chunk")
        {
            string text = update.TryGetProperty("content", out var content) && content.TryGetProperty("text", out var txt)
                ? txt.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(text)) return;
            lock (gate)
            {
                if (!collectors.TryGetValue(sessionId, out var c)) return;
                c.Buffer.Append(text);
                FlushIfNeeded(c);
            }
        }
    }

    void FlushIfNeeded(PromptCollector c)
    {
        if (config.ConsolidateMs <= 0) { FlushNow(c); return; }
        if (c.Buffer.ToString().Contains('\n')) { FlushNow(c); return; }
        c.ChunkTimer?.Dispose();
        c.ChunkTimer = new System.Threading.Timer(_ => FlushNow(c), null, config.ConsolidateMs, Timeout.Infinite);
    }

    void FlushNow(PromptCollector c)
    {
        c.ChunkTimer?.Dispose();
        c.ChunkTimer = null;
        if (c.Buffer.Length == 0) return;
        var session = sessions.FindSession(c.SessionId);
        if (session != null) session.AssistantText = c.Buffer.ToString();
    }

    /// <summary>尝试把结果交给等待者；若等待者已离开（权限/超时提前返回），返回 false。</summary>
    bool TryComplete(string sessionId, PromptOutcome outcome)
    {
        lock (gate)
        {
            if (!collectors.TryGetValue(sessionId, out var c) || c.Done) return false;
            c.Done = true;
            c.ChunkTimer?.Dispose();
            var session = sessions.FindSession(sessionId);
            if (session != null && outcome.Text.Length == 0) outcome.Text = session.AssistantText;
            c.Tcs.TrySetResult(outcome);
            return true;
        }
    }

    void Cleanup(string sessionId)
    {
        lock (gate)
        {
            if (collectors.TryGetValue(sessionId, out var c))
            {
                c.ChunkTimer?.Dispose();
                collectors.Remove(sessionId);
            }
        }
    }
}