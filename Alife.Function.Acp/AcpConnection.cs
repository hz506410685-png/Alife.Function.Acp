using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Alife.Function.Acp;

/// <summary>
/// ACP 传输层：一个 agent 进程的 JSON-RPC 2.0 over stdio（NDJSON）连接。
/// 进程管理照抄 Alife 自带 PythonService 的子进程模式；请求/响应/通知分发借鉴 mcacp transport。
/// </summary>
public sealed class AcpConnection : IAsyncDisposable
{
    readonly Process process;
    readonly StreamWriter writer;
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    readonly object writeLock = new();
    readonly CancellationTokenSource processCts = new();
    int nextId;

    /// <summary>收到 agent 的 notification（如 session/update）。参数：(method, params)。</summary>
    public event Action<string, JsonElement>? Notification;

    /// <summary>收到 agent 发来的 request（如 session/request_permission）。参数：(requestId, method, params)；返回 result；返回 null 视为 cancelled。</summary>
    public event Func<long, string, JsonElement, Task<JsonElement?>>? InboundRequest;

    /// <summary>agent 的 stderr / 内部错误输出。</summary>
    public event Action<string>? ErrorOutput;

    public bool IsRunning => process.HasExited == false;

    public AcpConnection(string command, string[] args, IReadOnlyDictionary<string, string>? env, string? cwd)
    {
        ProcessStartInfo psi = new()
        {
            FileName = command,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrEmpty(cwd) ? Environment.CurrentDirectory : cwd,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (args != null)
            foreach (string a in args) psi.ArgumentList.Add(a);
        if (env != null)
            foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        process = new Process { StartInfo = psi };
        process.Start();
        writer = process.StandardInput;
        _ = ReadLoopAsync(processCts.Token);
        _ = ReadErrorLoopAsync(processCts.Token);
    }

    /// <summary>发送 JSON-RPC 请求并等待响应（按 id 匹配）。</summary>
    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken ct)
    {
        int id = Interlocked.Increment(ref nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;

        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        });
        try
        {
            lock (writeLock) { writer.WriteLine(payload); writer.Flush(); }
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            return await tcs.Task.ConfigureAwait(false);
        }
        catch
        {
            pending.TryRemove(id, out _);
            throw;
        }
    }

    /// <summary>对 agent 发来的 request 写响应。result 为 null 时回 cancelled。</summary>
    public async Task RespondAsync(long id, JsonElement? result, CancellationToken ct = default)
    {
        object body = result.HasValue
            ? (object)new Dictionary<string, object?> { ["result"] = result.Value }
            : new Dictionary<string, object?> { ["result"] = new Dictionary<string, object?> { ["cancelled"] = new Dictionary<string, object?>() } };
        var merged = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id };
        foreach (var kv in ((Dictionary<string, object?>)body)) merged[kv.Key] = kv.Value;
        lock (writeLock) { writer.WriteLine(JsonSerializer.Serialize(merged)); writer.Flush(); }
        await Task.CompletedTask;
    }

    async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonElement msg;
                try { msg = JsonDocument.Parse(line).RootElement; }
                catch { continue; }

                if (msg.TryGetProperty("id", out JsonElement idEl))
                {
                    if (msg.TryGetProperty("method", out _))
                    {
                        long reqId = idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64() : -1;
                        string method = msg.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
                        JsonElement prms = msg.TryGetProperty("params", out var p) ? p : default;
                        _ = HandleInboundAsync(reqId, method, prms);
                    }
                    else if (idEl.ValueKind == JsonValueKind.Number && pending.TryRemove(idEl.GetInt32(), out var tcs))
                    {
                        if (msg.TryGetProperty("error", out JsonElement err))
                            tcs.TrySetException(new Exception("ACP error: " + err.GetRawText()));
                        else
                            tcs.TrySetResult(msg.TryGetProperty("result", out JsonElement res) ? res.Clone() : default);
                    }
                }
                else if (msg.TryGetProperty("method", out JsonElement m))
                {
                    string method = m.GetString() ?? "";
                    JsonElement prms = msg.TryGetProperty("params", out JsonElement p) ? p : default;
                    Notification?.Invoke(method, prms.Clone());
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ErrorOutput?.Invoke("stdout 读取异常: " + ex.Message); }
        finally
        {
            foreach (var kv in pending) kv.Value.TrySetException(new IOException("agent 进程已退出"));
            pending.Clear();
        }
    }

    async Task HandleInboundAsync(long id, string method, JsonElement prms)
    {
        try
        {
            JsonElement? result = null;
            if (InboundRequest != null)
                result = await InboundRequest(id, method, prms).ConfigureAwait(false);
            await RespondAsync(id, result).ConfigureAwait(false);
        }
        catch (Exception ex) { ErrorOutput?.Invoke("入站请求处理异常: " + ex.Message); }
    }

    async Task ReadErrorLoopAsync(CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(ct).ConfigureAwait(false)) != null)
                if (!string.IsNullOrWhiteSpace(line)) ErrorOutput?.Invoke(line);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        processCts.Cancel();
        try { if (process.HasExited == false) process.Kill(entireProcessTree: true); } catch { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        process.Dispose();
    }
}