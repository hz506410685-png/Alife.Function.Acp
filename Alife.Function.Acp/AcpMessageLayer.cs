using System;
using System.Text.RegularExpressions;

namespace Alife.Function.Acp;

/// <summary>
/// 消息层：借鉴 dingshuxin353/acp 的 Agent 间消息内容格式（Ask/Briefing/Checkpoint）+ 脱敏。
/// 露露派活时生成结构化简报，降低 agent 误解、可裁剪、可追踪。
/// </summary>
public static class AcpMessageLayer
{
    /// <summary>生成 Ask 任务消息（单次任务）。</summary>
    public static string BuildAsk(string from, string ask, string? context, string? expect)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("--- ACP START ---");
        sb.AppendLine("## Type");
        sb.AppendLine("Ask");
        sb.AppendLine("## From");
        sb.AppendLine(from);
        sb.AppendLine("## Ask");
        sb.AppendLine(ask);
        if (!string.IsNullOrWhiteSpace(context))
        {
            sb.AppendLine("## Context");
            sb.AppendLine(context);
        }
        if (!string.IsNullOrWhiteSpace(expect))
        {
            sb.AppendLine("## Expect");
            sb.AppendLine(expect);
        }
        sb.AppendLine("--- ACP END ---");
        return sb.ToString();
    }

    /// <summary>生成 Briefing 项目交接消息（首次派活，整包上下文）。</summary>
    public static string BuildBriefing(string from, string purpose, string? project, string? architecture, string? knownIssues, string? keyDecisions, string ask, string? expect)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("--- ACP START ---");
        sb.AppendLine("## Type");
        sb.AppendLine("Briefing");
        sb.AppendLine("## From");
        sb.AppendLine(from);
        sb.AppendLine("## Purpose");
        sb.AppendLine(purpose);
        if (!string.IsNullOrWhiteSpace(project)) { sb.AppendLine("## Project"); sb.AppendLine(project); }
        if (!string.IsNullOrWhiteSpace(architecture)) { sb.AppendLine("## Architecture"); sb.AppendLine(architecture); }
        if (!string.IsNullOrWhiteSpace(knownIssues)) { sb.AppendLine("## Known Issues"); sb.AppendLine(knownIssues); }
        if (!string.IsNullOrWhiteSpace(keyDecisions)) { sb.AppendLine("## Key Decisions Made"); sb.AppendLine(keyDecisions); }
        sb.AppendLine("## Ask");
        sb.AppendLine(ask);
        if (!string.IsNullOrWhiteSpace(expect)) { sb.AppendLine("## Expect"); sb.AppendLine(expect); }
        sb.AppendLine("--- ACP END ---");
        return sb.ToString();
    }

    /// <summary>脱敏：替换 API key、Bearer token、Windows 绝对路径等（默认开）。</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = Regex.Replace(text, @"sk-[A-Za-z0-9\-_]{8,}", "sk-***");
        text = Regex.Replace(text, @"(?i)bearer\s+[A-Za-z0-9\-._~+/]+=*", "Bearer ***");
        text = Regex.Replace(text, @"[A-Za-z]:\\[^\s`""<>|:]*", "C:\\***");
        text = Regex.Replace(text, @"(?i)(api[_-]?key|password|secret|token)\s*[:=]\s*\S+", "$1=***");
        return text;
    }
}