using System;
using System.Linq;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using TerrarAI.Content.Companion;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrarAI.Content.Systems;

// Addressed /ai speech only. State access and commits occur in the game pump.
public sealed class CompanionDialogueSystem : ModSystem
{
    private static Task<string>? _request;
    private static CancellationTokenSource? _cancellation;
    private static CompanionStore? _store;
    private static Guid _session;
    private static ControlStamp _controlStamp;
    private static string _text = "", _source = "", _turnId = "";
    private static string? _episodeId, _resumeCommand;
    private static int _player;
    private static long _startedAt;
    private static bool _feedbackOnly, _resolving;
    private static ClarificationTicket? _pending;
    private static DialogueDecision? _resolvedScope;
    private static int _clarificationAttempts;
    private static string? _previousQuestion;
    private static bool _wasScopeClarification;
    internal static bool AwaitingClarification => _pending != null;
    private static readonly System.Text.RegularExpressions.Regex SetsRule =
        new("以后|今后|记住|记得|下次|每次|总是|一直都|别再|不要再|不准|不许|禁止|先问|不用问|叫我");
    internal static bool IsBusy => _request != null;
    private static readonly JsonSerializerOptions PromptJson = new()
        { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    internal static void Submit(Player player, string text, bool feedbackOnly = false)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > CompanionProtocol.MaxCommandLength)
            throw new ArgumentException("Message must contain 1–240 characters.");
        var store = CompanionMemorySystem.Store ?? throw new InvalidOperationException("World memory is unavailable.");
        var companion = ChatCoordinator.GetOrCreateCompanion(player)
            ?? throw new InvalidOperationException("Could not spawn companion.");
        // "砍完了吗" asks about the work; answer from the record and keep working.
        if (!feedbackOnly && _pending == null && _request == null && StatusQuery.IsStatusQuestion(text))
        {
            string answer = companion.DescribeStatus();
            store.AddDialogue(text, answer);
            CompanionMemorySystem.Record("status_answered", null, null, new { text, answer, modelRequest = false }, null, null);
            CompanionUISystem.Message("Qwen：" + answer, Color.LightGreen);
            return;
        }
        // Everything except standing rules goes to the agent: it picks tools (check the bag, craft, place,
        // chop, mine, build, ask the knowledge base ...) step by step and reports what really happened.
        // Rules ("以后夜里先问我") still go through the preference model first, so they are remembered.
        if (!feedbackOnly && _pending == null && _request == null && (CompanionAgent.AwaitingAnswer || !SetsRule.IsMatch(text)))
        {
            CompanionMemorySystem.Record("agent_routed", null, null, new { text, answering = CompanionAgent.AwaitingAnswer }, null, null);
            CompanionAgent.Start(player, text);
            return;
        }
        string? previousTask = companion.CurrentCommandForDialogue;
        string? previousEpisode = companion.CurrentEpisodeForDialogue;
        var pending = _pending;
        bool continuing = pending?.Matches(CompanionMemorySystem.Session, player.whoAmI,
            store.Memory.RuleRevision, Main.dayTime, DateTimeOffset.UtcNow) == true;
        string? resumed = continuing ? pending!.ResumeCommand : previousTask;
        string source = continuing ? pending!.Source + "\n玩家补充：" + text : text;
        if (source.Length > 512) throw new ArgumentException("澄清内容太长，请 /ai stop 后重新简短说明。");
        if (continuing) { _pending = null; ResetRequest(); } else Cancel();
        CompanionCommandService.Advance();
        _controlStamp = CompanionCommandService.Capture();
        // Preserve linkage before StopCommand clears it. Unprocessed corrections stop old work.
        companion.StopCommand("player_dialogue");
        _store = store;
        _session = CompanionMemorySystem.Session;
        _text = text;
        _source = source;
        _resumeCommand = resumed;
        _turnId = Guid.NewGuid().ToString("N");
        _player = player.whoAmI;
        _feedbackOnly = feedbackOnly || (continuing && pending!.FeedbackOnly);
        _resolving = continuing;
        _clarificationAttempts = continuing ? pending!.Attempts + 1 : 1;
        _previousQuestion = continuing ? pending!.Question : null;
        _wasScopeClarification = continuing && pending!.ScopeCandidates != null;
        bool referencesPrevious = feedbackOnly || new[] { "刚才", "上次", "那次" }.Any(text.Contains);
        string? related = referencesPrevious ? previousEpisode ?? store.Memory.LastTask?.EpisodeId : null;
        _episodeId = continuing ? pending!.EpisodeId : CompanionMemorySystem.Episodes!.Begin(_turnId, text,
            CompanionObservation.Capture(player, companion.NPC), related);
        string unsupported = PreferenceProtocol.CapabilityNotice(text);
        if (unsupported.Length > 0) CompanionUISystem.Message("Qwen：" + unsupported, Color.Orange);
        if (continuing)
            CompanionMemorySystem.Record("clarification_answered", null, null,
                new { question = pending!.Question, answer = text }, _episodeId, _turnId);
        if (referencesPrevious)
            CompanionMemorySystem.Record("player_feedback", null, null,
                new { text, relatedEpisodeId = related, relation = related == null ? "unresolved" : "explicit_previous_task" }, _episodeId, _turnId);
        if (continuing && PreferenceProtocol.ResolveScope(pending!.ScopeCandidates, text, source) is { } resolved)
        {
            _resolvedScope = resolved;
            _request = Task.FromResult("");
            CompanionMemorySystem.Record("clarification_scope_resolved", null, null,
                new { answer = text, updates = resolved.Updates, origin = "explicit_scope_answer", modelRequest = false }, _episodeId, _turnId);
            return;
        }
        string preferenceText = PreferenceProtocol.PreferenceText(text);
        if (preferenceText.Length == 0 && unsupported.Length > 0)
        {
            _resolvedScope = new DialogueDecision("chat", unsupported, Array.Empty<PreferenceUpdate>());
            _request = Task.FromResult("");
            CompanionMemorySystem.Record("unsupported_request", null, null,
                new { text, notice = unsupported, modelRequest = false }, _episodeId, _turnId);
            return;
        }
        var state = JsonSerializer.Serialize(new
        {
            message = preferenceText, originalMessage = continuing ? PreferenceProtocol.PreferenceText(pending!.Source) : null,
            clarificationQuestion = continuing ? pending!.Question : null,
            mode = _feedbackOnly ? "feedback_only" : "conversation",
            previousTask = CompanionProtocol.Clip(previousTask ?? store.Memory.LastTask?.Command, 120),
            preferences = store.PromptMemory(text),
            dialogue = store.Memory.Dialogue.TakeLast(2).Select(line => new { line.Role,
                text = CompanionProtocol.Clip(PreferenceProtocol.PreferenceText(line.Text), 80) })
        }, PromptJson);
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(CompanionMemorySystem.WorldToken);
        _startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        CompanionMemorySystem.Record("dialogue_request", null, null, new { text, source, state,
            system = PreferenceProtocol.SystemPrompt, model = TerrarAI_Config.Get().Model,
            temperature = 0, maxTokens = 256 }, _episodeId, _turnId);
        try
        {
            _request = TerrarAI.RequireClient().AnalyzeDialogueAsync(state, _source, _cancellation.Token);
            _ = _request.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch { Cancel(); throw; }
    }

    public override void PostUpdateWorld()
    { if (Main.netMode == NetmodeID.SinglePlayer) ProcessPending(); }

    internal static void ProcessPending()
    {
        if (_pending != null && !_pending.Matches(CompanionMemorySystem.Session, _pending.Player,
            CompanionMemorySystem.Store?.Memory.RuleRevision ?? -1, Main.dayTime, DateTimeOffset.UtcNow))
        {
            CompanionMemorySystem.Episodes?.Close(_pending.EpisodeId, "clarification_expired_or_context_changed");
            _pending = null;
        }
        if (_request == null) return;
        if (_session != CompanionMemorySystem.Session || !ReferenceEquals(_store, CompanionMemorySystem.Store) ||
            !_controlStamp.Matches(CompanionMemorySystem.Session, CompanionCommandService.Revision))
        { Cancel(); return; }
        if (!_request.IsCompleted) return;
        bool handedOff = false;
        string reason = "dialogue_only";
        try
        {
            if (_request.IsCanceled) { reason = "cancelled"; return; }
            if (_request.IsFaulted) throw _request.Exception!.GetBaseException();
            var player = Main.player[_player];
            if (!player.active || player.dead) { reason = "player_unavailable"; return; }
            var decision = _resolvedScope;
            if (decision == null)
            {
                string raw = _request.Result;
                CompanionMemorySystem.Record("dialogue_response", null, null, new { raw,
                    latencyMs = System.Diagnostics.Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds }, _episodeId, _turnId);
                decision = PreferenceProtocol.Parse(raw, _source);
            }
            if (decision.Intent == "clarify")
            {
                if (_resolving && (_clarificationAttempts >= 3 || _clarificationAttempts >= 2 &&
                    (decision.Reply == _previousQuestion || _wasScopeClarification && decision.ScopeCandidates != null)))
                {
                    reason = "clarification_loop_stopped";
                    string failure = "我没能完成这次澄清，暂未保存规则。请把采集要求单独重新说一次，例如：以后白天采集先问，夜里不用。";
                    _store!.AddDialogue(_text, failure);
                    CompanionUISystem.Message("Qwen：" + failure, Color.Orange);
                    return;
                }
                _pending = new ClarificationTicket(_session, _player, _episodeId!, _source, decision.Reply,
                    _resumeCommand, _feedbackOnly, _store!.Memory.RuleRevision, Main.dayTime, DateTimeOffset.UtcNow.AddMinutes(2),
                    decision.ScopeCandidates, _clarificationAttempts);
                CompanionMemorySystem.Record("clarification_requested", null, null,
                    new { question = decision.Reply, resumeTask = _resumeCommand }, _episodeId, _turnId);
                _store.AddDialogue(_text, decision.Reply);
                CompanionUISystem.Message("Qwen：" + decision.Reply, Color.LightBlue);
                handedOff = true; // Question retains this episode.
                return;
            }
            var changes = _store!.ApplyPreferences(decision.Updates, _source, _turnId);
            foreach (var change in changes)
                CompanionMemorySystem.Record("preference_learned", null, null, change, _episodeId, _turnId);
            if (_resolving)
                CompanionMemorySystem.Record("clarification_resolved", null, null,
                    new { intent = decision.Intent, changes = changes.Count, ruleRevision = _store.Memory.RuleRevision }, _episodeId, _turnId);
            string reply = changes.Count > 0
                ? "记住了：" + string.Join("；", changes.Select(PreferenceProtocol.Describe)) + "。"
                : decision.Updates.Count > 0 ? "这个世界已经记着这些偏好了。" : decision.Reply;
            string capabilityNotice = PreferenceProtocol.CapabilityNotice(_source);
            if (capabilityNotice.Length > 0) reply = changes.Count > 0 ? reply + " " + capabilityNotice : capabilityNotice;
            _store.AddDialogue(_text, reply);
            CompanionUISystem.Message("Qwen：" + reply, Color.LightGreen);
            string? command = decision.Intent == "task" ? _text : decision.Updates.Count > 0 ? _resumeCommand : null;
            if (!_feedbackOnly && command != null)
            {
                if (command.Length > CompanionProtocol.MaxCommandLength)
                    throw new ArgumentException("规则已处理，但澄清后的任务太长，请重新简短下达任务。");
                CompanionAgent.Start(player, command);
            }
        }
        catch (Exception ex)
        {
            reason = "dialogue_error";
            CompanionMemorySystem.Record("dialogue_error", null, null, new { error = ex.Message }, _episodeId, _turnId);
            CompanionUISystem.Message("Qwen：这次没有启动新任务。" + ex.Message, Color.OrangeRed);
        }
        finally
        {
            if (!handedOff && _episodeId != null) CompanionMemorySystem.Episodes?.Close(_episodeId, reason);
            ResetRequest();
        }
    }

    private static void ResetRequest()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        _request = null;
        _store = null;
        _episodeId = null;
        _resumeCommand = null;
        _resolving = false;
        _resolvedScope = null;
        _previousQuestion = null;
        _wasScopeClarification = false;
    }

    internal static void Cancel()
    {
        if (_episodeId != null) CompanionMemorySystem.Episodes?.Close(_episodeId, "dialogue_cancelled");
        if (_pending != null) CompanionMemorySystem.Episodes?.Close(_pending.EpisodeId, "clarification_cancelled");
        _pending = null;
        ResetRequest();
    }
    public override void OnWorldUnload() => Cancel();
    public override void ClearWorld() => Cancel();
    public override void Unload() => Cancel();
}
