using System;
using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using TerrarAI.Content.Companion;
using Terraria;
using Terraria.ModLoader;

namespace TerrarAI.Content.Systems;

public sealed class CompanionInquirySystem : ModSystem
{
    private static Task<string>? _request;
    internal static string WikiStatus { get; private set; } = "离线 Wiki 尚未查询；支持导入 .pkg／SQLite。";
    internal static string WikiPath => Path.Combine(Main.SavePath, "ModData", "TerrarAI", "knowledge", "wiki.db");
    private static CancellationTokenSource? _cancellation;
    private static Guid _session;
    private static ControlStamp _controlStamp;
    private static CompanionStore? _store;
    private static int _player;
    private static string? _episode, _turn;
    private static long _startedAt;
    internal static bool IsBusy => _request != null;

    internal static void Submit(Player player, string question)
    {
        if (CompanionUISystem.IsBusy) throw new InvalidOperationException("请等当前回复，或先点击停止。");
        if (string.IsNullOrWhiteSpace(question) || question.Length > CompanionProtocol.MaxCommandLength)
            throw new ArgumentException("问题请写在1–240字内。");
        _store = CompanionMemorySystem.Store ?? throw new InvalidOperationException("世界记忆尚未就绪。");
        _session = CompanionMemorySystem.Session;
        _controlStamp = CompanionCommandService.Capture();
        _player = player.whoAmI;
        _turn = Guid.NewGuid().ToString("N");
        var world = CompanionWorldInfo.Capture(player);
        _episode = CompanionMemorySystem.Episodes?.Begin(_turn, question, world);
        var state = JsonSerializer.Serialize(new { question, gameVersion = Main.versionNumber, world = CompanionWorldInfo.ForPrompt(world, true) }, new JsonSerializerOptions
                { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) });
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(CompanionMemorySystem.WorldToken);
        _startedAt = Stopwatch.GetTimestamp();
        CompanionMemorySystem.Record("inquiry_request", null, null,
            new { question, state, system = WikiInquiryProtocol.SystemPrompt, snapshot = "at_submission" }, _episode, _turn);
        try
        {
            string version = WikiKnowledge.NormalizeVersion(Main.versionNumber);
            // The immutable result carries retrieval provenance; workers never write game/UI state.
            // The brain gets the full picture (whole inventory, tools, NPCs, bosses) as plain JSON,
            // serialized here on the game thread so the worker never touches game objects.
            string brainState = JsonSerializer.Serialize(CompanionWorldInfo.ForBrain(player, world), new JsonSerializerOptions
                { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) });
            _request = TerrarAI.RequireClient().AskWikiAsync(WikiPath, version, question,
                CompanionWorldInfo.ForPrompt(world, true), _cancellation.Token, brainState);

            _ = _request.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch { Cancel(); throw; }
    }

    public override void PostUpdateWorld() => ProcessPending();
    internal static void ProcessPending()
    {
        if (_request == null) return;
        if (_session != CompanionMemorySystem.Session || !ReferenceEquals(_store, CompanionMemorySystem.Store) ||
            !_controlStamp.Matches(CompanionMemorySystem.Session, CompanionCommandService.Revision))
        { Cancel(); return; }
        if (!_request.IsCompleted) return;
        string reason = "inquiry_answered";
        try
        {
            if (_request.IsCanceled) { reason = "inquiry_cancelled"; return; }
            if (_request.IsFaulted) throw _request.Exception!.GetBaseException();
            var player = Main.player[_player];
            if (!player.active || player.dead) { reason = "player_unavailable"; return; }
            var response = JsonSerializer.Deserialize<WikiInquiryResult>(_request.Result)
                ?? throw new FormatException("Wiki 问答缺少检索依据。");
            var context = response.Wiki ?? throw new FormatException("Wiki 问答缺少检索依据。");
            WikiStatus = context.Status;
            var result = WikiInquiryProtocol.Parse(response.Raw, context);
            string answer = WikiInquiryProtocol.Render(result, context);
            CompanionMemorySystem.Record("wiki_retrieval", null, null, new { context, result.Basis, result.Citations,
                provenance = WikiInquiryProtocol.Provenance(result, context) }, _episode, _turn);

            CompanionMemorySystem.Record("inquiry_response", null, null,
                new { answer, latencyMs = Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds }, _episode, _turn);
            // No dialogue/preference commit or action dispatch is reachable from this pump.
            CompanionUISystem.Message("Qwen · 问答：" + answer, Color.LightGreen);
        }
        catch (Exception ex)
        {
            reason = "inquiry_error";
            CompanionMemorySystem.Record("inquiry_error", null, null, new { error = ex.Message }, _episode, _turn);
            CompanionUISystem.Message("问答暂时没成功：" + ex.Message, Color.Orange);
        }
        finally
        {
            if (_episode != null) CompanionMemorySystem.Episodes?.Close(_episode, reason);
            Reset();
        }
    }

    private static void Reset()
    {
        _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = null;
        _request = null; _store = null; _episode = _turn = null;
    }
    internal static void Cancel()
    {
        if (_episode != null && ReferenceEquals(_store, CompanionMemorySystem.Store))
            CompanionMemorySystem.Episodes?.Close(_episode, "inquiry_cancelled");
        Reset();
    }
    public override void OnWorldUnload() { Cancel(); WikiStatus = "离线 Wiki 尚未查询；支持导入 .pkg／SQLite。"; }
    public override void ClearWorld() => Cancel();
    public override void Unload() => Cancel();
}
