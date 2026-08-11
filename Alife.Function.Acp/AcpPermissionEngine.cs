using System.Collections.Generic;
using System.Text.Json;

namespace Alife.Function.Acp;

/// <summary>
/// 权限策略：allow_all / allow_readonly / deny_all / operator。
/// 借鉴 mcacp PermissionEngine 的 4 策略；Alife 无 elicitation，故 operator 由露露审批。
/// </summary>
public static class AcpPermissionEngine
{
    /// <summary>
    /// 按策略自动决策。返回 null 表示需要人工（露露）审批。
    /// prms 为 session/request_permission 的 params（含 toolCall/options）。
    /// </summary>
    public static JsonElement? AutoDecide(string policy, JsonElement prms)
    {
        var options = ParseOptions(prms);
        if (options.Count == 0) return Cancelled();

        switch (policy.ToLowerInvariant())
        {
            case "allow_all":
                return SelectFirstAllow(options);
            case "deny_all":
                return SelectFirstReject(options) ?? Cancelled();
            case "allow_readonly":
                return IsReadOnly(prms, options) ? SelectFirstAllow(options) : null;
            case "operator":
            default:
                return null;
        }
    }

    /// <summary>根据露露的同意/拒绝构造对 agent 的决策响应。</summary>
    public static JsonElement? BuildDecision(PendingPermission pending, bool allow)
    {
        if (allow)
            return SelectFirstAllow(pending.Options) ?? SelectFirstReject(pending.Options) ?? Selected(pending.Options.Count > 0 ? pending.Options[0].OptionId : "allow");
        return SelectFirstReject(pending.Options) ?? Cancelled();
    }

    public static List<PermissionOption> ParseOptions(JsonElement prms)
    {
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
        return options;
    }

    static bool IsReadOnly(JsonElement prms, List<PermissionOption> options)
    {
        string title = "";
        if (prms.TryGetProperty("toolCall", out var tc) && tc.TryGetProperty("title", out var t))
            title = (t.GetString() ?? "").ToLowerInvariant();

        string[] writeHints = { "write", "edit", "delete", "create", "move", "run", "execute", "terminal", "bash", "shell", "apply", "修改", "写入", "删除", "创建", "执行", "运行", "改" };
        foreach (var hint in writeHints)
            if (title.Contains(hint)) return false;
        return true;
    }

    static JsonElement? SelectFirstAllow(List<PermissionOption> options)
    {
        foreach (var o in options)
            if (o.Kind is "allow_once" or "allow_always") return Selected(o.OptionId);
        return options.Count > 0 ? Selected(options[0].OptionId) : null;
    }

    static JsonElement? SelectFirstReject(List<PermissionOption> options)
    {
        foreach (var o in options)
            if (o.Kind is "reject_once" or "reject_always") return Selected(o.OptionId);
        return null;
    }

    static JsonElement Selected(string optionId)
    {
        using var doc = JsonDocument.Parse($"{{\"selected\":{{\"optionId\":{JsonSerializer.Serialize(optionId)}}}}}");
        return doc.RootElement.Clone();
    }

    static JsonElement Cancelled()
    {
        using var doc = JsonDocument.Parse("{\"cancelled\":{}}");
        return doc.RootElement.Clone();
    }
}