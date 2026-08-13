using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;
using Alife.Function.FunctionCaller;

namespace Alife.Function.Acp;

/// <summary>
/// ACP 项目管理插件：让露露通过 Agent Client Protocol 指挥本机编码 agent（如 Codex）。
/// 架构参考 Alife.Function.Mcp（ChatBehaviour + XmlHandler + Poke），
/// 会话/权限/prompt 编排借鉴 mcacp，命名会话与目录边界借鉴 acpx，消息层借鉴 dingshuxin/acp。
/// </summary>
[Module("ACP 项目管理",
    "通过 Agent Client Protocol 指挥本机编码 agent（Codex 等）。露露可派活、催活、审批权限、收回任务，并管理持久化会话。",
    defaultCategory: "Alife 官方/功能底座")]
public class AcpService(
    XmlFunctionCaller functionCaller,
    Interactor<AcpService> interactor) :
    ChatBehaviour,
    IConfigurable<AcpConfig>
{
    public AcpConfig Configuration { get; set; } = null!;

    AcpSessionManager? manager;
    AcpPromptHandler? prompts;

    AcpSessionManager Manager => manager ?? throw new InvalidOperationException("插件尚未初始化");
    AcpPromptHandler Prompts => prompts ?? throw new InvalidOperationException("插件尚未初始化");

    protected override async Task OnAwake()
    {
        manager = new AcpSessionManager(Configuration, AlifePath.StorageFolderPath);
        prompts = new AcpPromptHandler(manager, Configuration);
        prompts.PromptFinished += OnPromptFinished;

        foreach (var cfg in Configuration.Agents)
        {
            var client = manager.GetOrCreateAgent(cfg.Name);
            client.PermissionRequestHandler = (id, prms) => HandlePermissionRequestAsync(id, prms);
        }

        XmlHandler handler = new(this)
        {
            Description = "让露露担任项目经理，指挥本机编码 agent（Codex）干活。先 agent_list 看可用 agent，再 agent_start 启动，然后用 派活 下发任务；agent 要权限时用 审批 决定；任务太久用 催活，想停用 收回。",
            Explanation = "派活 的内容建议用 ACP 简报格式（--- ACP START --- 开头）或直接写清任务、上下文、验收标准。agent 与工作目录需在插件配置的 AllowedWorkDirs 白名单内。",
        };
        functionCaller.RegisterHandler(handler, cancellationToken: DestroyCancellationToken);
        functionCaller.AddPlainAreas(nameof(派活));

        await Task.CompletedTask;
    }

    protected override async Task OnDestroy()
    {
        if (manager != null) await manager.DisposeAsync().ConfigureAwait(false);
        manager = null;
        prompts = null;
    }

    // ---------- 露露的函数 ----------

    [XmlFunction(FunctionMode.OneShot)]
    [Description("列出已配置的编码 agent 及其运行状态")]
    public Task AgentList()
    {
        var sb = new StringBuilder();
        foreach (var cfg in Configuration.Agents)
        {
            bool running = false;
            try
            {
                var client = Manager.GetOrCreateAgent(cfg.Name);
                running = client.IsRunningSafe();
            }
            catch { }
            sb.AppendLine($"- {cfg.Name}（{(running ? "运行中" : "未启动")}）默认目录：{cfg.DefaultCwd}");
        }
        if (Configuration.Agents.Count == 0)
            sb.AppendLine("尚未配置任何 agent，请在插件配置里添加。");
        sb.AppendLine($"允许的工作目录：{string.Join("；", Configuration.AllowedWorkDirs)}");
        interactor.Poke(sb.ToString());
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("启动并初始化指定 agent（懒启动，之后派活会自动启动）")]
    public async Task AgentStart(
        [Description("agent 名（agent_list 可见）")] string agent,
        [Description("工作目录，需在白名单内；留空用 agent 默认目录")] string cwd = "")
    {
        string dir = Manager.ResolveCwd(cwd, agent);
        var client = Manager.GetOrCreateAgent(agent);
        await client.EnsureStartedAsync(DestroyCancellationToken).ConfigureAwait(false);
        interactor.Poke($"agent「{agent}」已就绪，工作目录：{dir}");
    }

    [XmlFunction(FunctionMode.Content)]
    [Description("派活给编码 agent：复用固定/命名会话（同一窗口），发送任务（建议 ACP 简报格式），等待结果")]
    public async Task 派活(
        XmlExecutorContext context,
        [XmlContent] string 任务,
        [Description("agent 名，默认 codex")] string agent = "codex",
        [Description("会话名；同名会话会复用同一窗口（不传则用配置的固定会话）")] string 会话名 = "",
        [Description("权限策略：operator（默认，每步审批）/allow_readonly（只读放行）/allow_all（全放行）")] string 权限 = "",
        [Description("期望交付格式，如：返回改动清单与验证方式")] string 验收标准 = "")
    {
        if (context.CallMode != CallMode.Closing) return;
        string content = context.FullContent.Trim();
        if (content.Length == 0)
        {
            interactor.Poke("派活内容不能为空，请用 <派活>...</派活> 包裹任务说明。");
            return;
        }

        // 不指定会话名时，默认复用固定窗口（DefaultSessionName）；留空则每次新建会话
        string name = string.IsNullOrWhiteSpace(会话名) ? Configuration.DefaultSessionName : 会话名.Trim();

        ActiveSession? session = null;
        if (string.IsNullOrWhiteSpace(name) == false)
        {
            // 1) 内存中的活动会话（本窗口）
            session = Manager.FindSession(name);
            // 2) 内存没有 → 从磁盘加载同名未关闭会话（跨重启复用同一窗口）
            if (session == null)
                session = await Manager.LoadPersistedSessionAsync(agent, name, DestroyCancellationToken).ConfigureAwait(false);
        }

        if (session == null)
        {
            string dir = Manager.ResolveCwd(null, agent);
            session = await Manager.NewSessionAsync(agent, string.IsNullOrWhiteSpace(name) ? null : name, dir, 权限, DestroyCancellationToken).ConfigureAwait(false);
        }
        else
        {
            await session.Agent.EnsureStartedAsync(DestroyCancellationToken).ConfigureAwait(false);
        }

        string prompt = content.Contains("--- ACP START ---")
            ? content
            : AcpMessageLayer.BuildAsk("露露（Alife 陪伴智能体）", content, null, 验收标准);
        if (Configuration.RedactSecrets) prompt = AcpMessageLayer.Redact(prompt);

        string label = session.File.Name ?? session.File.SessionId;
        interactor.Poke($"已派活给「{agent}」会话「{label}」，任务进行中…（超时 {Configuration.PromptTimeoutSec}s 可催活）");
        var outcome = await Prompts.PromptAsync(session, prompt, DestroyCancellationToken).ConfigureAwait(false);
        await ReportOutcomeAsync(session, outcome).ConfigureAwait(false);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("催活：对进行中/超时的会话再次询问进度并取回结果")]
    public async Task 催活(
        [Description("会话名或 sessionId")] string 会话名)
    {
        var session = FindRequired(会话名);
        var outcome = await Prompts.PromptAsync(session,
            AcpMessageLayer.BuildAsk("露露（Alife 陪伴智能体）", "请继续完成当前任务，报告最新进度与最终交付。", null, null),
            DestroyCancellationToken).ConfigureAwait(false);
        await ReportOutcomeAsync(session, outcome).ConfigureAwait(false);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("审批 agent 的权限请求（同意或拒绝）")]
    public Task 审批(
        [Description("会话名或 sessionId")] string 会话名,
        [Description("同意 或 拒绝")] string 决定,
        [Description("理由（会记录到会话）")] string 理由 = "")
    {
        var session = FindRequired(会话名);
        bool allow = 决定 == "同意" || 决定 == "批准" || 决定 == "allow" || 决定 == "yes";
        bool ok = Prompts.ResolvePermission(session, allow);
        interactor.Poke(ok
            ? $"已{(allow ? "同意" : "拒绝")}会话「{session.File.Name ?? session.File.SessionId}」的权限请求。{(string.IsNullOrEmpty(理由) ? "" : "理由：" + 理由)}"
            : $"会话「{会话名}」当前没有待审批的权限请求。");
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("收回：取消并关闭一个会话（停止任务）")]
    public async Task 收回(
        [Description("会话名或 sessionId")] string 会话名)
    {
        var session = FindRequired(会话名);
        try
        {
            await session.Agent.CancelAsync(session.File.SessionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
        Manager.RemoveSession(session);
        interactor.Poke($"已取消并关闭会话「{会话名}」。");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("列出所有未关闭的会话（含命名会话与最近活跃时间）")]
    public Task 会话列表()
    {
        var sb = new StringBuilder();
        foreach (var cfg in Configuration.Agents)
        {
            var list = Manager.ListSessions(cfg.Name);
            sb.AppendLine($"【{cfg.Name}】{list.Count} 个会话");
            foreach (var f in list.Take(10))
                sb.AppendLine($"  - {(string.IsNullOrEmpty(f.Name) ? f.SessionId : f.Name + "（" + f.SessionId + "）")} | 策略:{f.PermissionPolicy} | 最近:{f.LastActiveAt} | {f.Cwd}");
        }
        if (sb.Length == 0) sb.AppendLine("暂无会话");
        interactor.Poke(sb.ToString());
        return Task.CompletedTask;
    }

    // ---------- 内部 ----------

    ActiveSession FindRequired(string nameOrId)
    {
        return Manager.FindSession(nameOrId)
            ?? throw new InvalidOperationException($"找不到会话「{nameOrId}」。用 会话列表 查看现有会话。");
    }

    Task<JsonElement?> HandlePermissionRequestAsync(long requestId, JsonElement prms)
    {
        string sessionId = prms.TryGetProperty("sessionId", out var s) ? s.GetString() ?? "" : "";
        var session = string.IsNullOrEmpty(sessionId) ? null : Manager.FindSession(sessionId);
        if (session == null) return Task.FromResult<JsonElement?>(null); // 取消未知会话的请求
        return Prompts.OnPermissionRequestAsync(session, requestId, prms);
    }

    async Task ReportOutcomeAsync(ActiveSession session, PromptOutcome outcome)
    {
        string label = session.File.Name ?? session.File.SessionId;
        if (string.IsNullOrEmpty(outcome.Error) == false)
        {
            interactor.Poke($"[{label}] 出错：{outcome.Error}");
            return;
        }
        if (outcome.NeedsApproval)
        {
            var pp = session.PendingPermission;
            string options = pp == null || pp.Options.Count == 0 ? "同意/拒绝" : string.Join(" / ", pp.Options.Select(o => o.Name));
            interactor.Poke($"[{label}] agent 请求权限：{pp?.Title}。选项：{options}。请用 审批 会话名={label} 决定=同意|拒绝，或 收回 取消任务。");
            return;
        }
        if (outcome.TimedOut)
        {
            interactor.Poke($"[{label}] 任务仍在进行（超过 {Configuration.PromptTimeoutSec}s）。可随时用 催活 会话名={label} 取回结果。当前进度：{Truncate(outcome.Text)}");
            return;
        }
        interactor.Poke($"[{label}] 完成（{outcome.StopReason}）：{Truncate(outcome.Text)}");
    }

    void OnPromptFinished(ActiveSession session, PromptOutcome outcome)
    {
        string label = session.File.Name ?? session.File.SessionId;
        string body = string.IsNullOrEmpty(outcome.Error) ? Truncate(outcome.Text) : "出错：" + outcome.Error;
        interactor.Poke($"[{label}] 后台任务已结束：{body}");
    }

    static string Truncate(string text, int max = 4000)
    {
        if (string.IsNullOrEmpty(text)) return "(无文本输出)";
        return text.Length <= max ? text : text[..max] + "…（已截断，完整结果可在会话记录查看）";
    }
}