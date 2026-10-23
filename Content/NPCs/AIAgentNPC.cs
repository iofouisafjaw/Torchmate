// TerrarAI replacement NPC adapter, 2026-10-20. See docs/source-replacement.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TerrarAI.Content.Actions;
using TerrarAI.Content.Companion;
using TerrarAI.Content.Systems;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
namespace TerrarAI.Content.NPCs;

public enum AgentState { Idle, Planning, Executing, Replanning, Completed, WaitingForApproval }
public sealed class AIAgentNPC : ModNPC
{
    private readonly Queue<AgentAction> _actionQueue = new();
    private readonly CompanionBehaviorController _behavior = new();
    private readonly FollowNavigation _followNavigation = new();
    private readonly HashSet<Point> _skipTargets = new();
    private AgentAction? _currentAction;
    private Task<string>? _plannerTask;
    private CancellationTokenSource? _plannerCancellation;
    private Guid _plannerSession;
    private ControlStamp _plannerControlStamp, _executionControlStamp;
    private string? _plannerTaskId, _plannerDecisionId, _taskId, _decisionId, _episodeId, _sourceTurnId;
    private string? _currentCommand, _replanContext, _lastReport, _shortfall, _oreWord, _reportTaskId;
    private string? _lastPlannerError, _lastGatherFailure;
    private string _statusMessage = "", _anchor = "companion";
    private bool _taskClosed = true, _executionApproved, _planningDay, _executionDay, _constructionDisplay, _combatMovement;
    private long _planningRevision, _executionRevision, _decisionStartedAt, _planningStartTick;
    private long _executionStartTick, _actionStartTick, _reportDueTick;
    private int _decisionCount, _planningRetryCount, _verifiedEffects, _goalDone, _autoCollectTicksRemaining;
    private Player? _commander;
    private Point? _programTarget, _lockedMineTarget;
    private TaskGoal _goal = TaskGoal.None;
    private ApprovalTicket? _pendingApproval;
    private CompanionDecision? _executionDecision;
    private object? _actionBefore;
    private Dictionary<int, int>? _inventoryAtStart;
    internal CompanionMode Mode => _behavior.Mode;
    internal float? WaitingAtX => _behavior.WaitX;
    internal AgentState InterfaceState => State;
    internal string? CurrentCommandForDialogue => _taskClosed ? null : _currentCommand;
    internal string? CurrentEpisodeForDialogue => _taskClosed ? null : _episodeId;
    public bool HasPendingApproval => CompanionGardenSystem.Asking || StructureWorkshop.ArenaAsking ||
        CompanionExpeditionSystem.Asking || CraftChainSystem.Asking || CompanionConstructionSystem.SupplyAsking ||
        State == AgentState.WaitingForApproval && _pendingApproval != null;
    private AgentState State
    {
        get => (AgentState)(int)NPC.ai[0];
        set { if (ServerAuthority.IsServer && NPC.ai[0] != (int)value) { NPC.ai[0] = (int)value; NPC.netUpdate = true; UpdateStatus(); } }
    }

    public override string Texture => $"Terraria/Images/NPC_{NPCID.GoblinScout}";
    public override bool CheckActive() => false;
    public override void SetStaticDefaults() => Main.npcFrameCount[Type] = 16;
    public override void SetDefaults()
    {
        NPC.aiStyle = -1; NPC.damage = 0; NPC.knockBackResist = 0;
        NPC.width = NPC.height = 40; NPC.lifeMax = 250; NPC.stepSpeed = .6f;
        NPC.noGravity = NPC.noTileCollide = false;
        NPC.friendly = NPC.dontTakeDamage = NPC.dontTakeDamageFromHostiles = true;
    }
    public override void FindFrame(int frameHeight)
    {
        if (Math.Abs(NPC.velocity.X) <= .1f) { NPC.frame.Y = 0; NPC.frameCounter = 0; return; }
        if (++NPC.frameCounter < 6) return;
        NPC.frameCounter = 0;
        NPC.frame.Y = (NPC.frame.Y / frameHeight + 1) % Main.npcFrameCount[Type] * frameHeight;
    }
    public override void OnSpawn(IEntitySource source) { _behavior.Follow(); State = AgentState.Idle; UpdateStatus(); }
    public override void OnKill()
    {
        CompanionCombatSystem.Cancel("同伴已离场，战斗已停止。");
        CompanionExpeditionSystem.Cancel("同伴已离场，探路已停止。");
        StopCommand("companion_removed");
    }
    public override void SendExtraAI(BinaryWriter writer)
    { writer.Write((int)State); writer.Write(_statusMessage); writer.Write(_lastPlannerError ?? ""); }
    public override void ReceiveExtraAI(BinaryReader reader)
    { NPC.ai[0] = reader.ReadInt32(); _statusMessage = reader.ReadString(); _lastPlannerError = reader.ReadString(); }
    public override void PostDraw(SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
    {
        CompanionCombatSystem.Draw(NPC, spriteBatch, screenPos, drawColor);
        if (_statusMessage.Length == 0) return;
        var size = FontAssets.MouseText.Value.MeasureString(_statusMessage);
        var tint = State == AgentState.WaitingForApproval ? Color.Gold : State == AgentState.Executing ? Color.LightGreen : Color.LightBlue;
        Utils.DrawBorderString(spriteBatch, _statusMessage, NPC.Top - screenPos - new Vector2(size.X / 2, 24), tint, .9f);
    }
    public override void AI()
    {
        if (!ServerAuthority.IsServer) { UpdateFacing(); return; }
        if (TickOwnedJob()) { UpdateFacing(); return; }
        if (_constructionDisplay) { _constructionDisplay = false; UpdateStatus(); }
        TickReport();
        switch (State)
        {
            case AgentState.Idle:
                if (Mode == CompanionMode.Wait) NPC.velocity.X = _behavior.WaitingVelocity(NPC.Center.X, NPC.velocity.X);
                else Follow();
                break;
            case AgentState.Planning: case AgentState.Replanning: TickPlanning(); break;
            case AgentState.Executing: TickExecuting(); break;
            case AgentState.WaitingForApproval: MovementHelper.Hold(NPC); ValidateApproval(); break;
            case AgentState.Completed: MovementHelper.Hold(NPC); State = AgentState.Idle; break;
        }
        TickAutoCollect(); UpdateFacing();
    }
    private bool TickOwnedJob()
    {
        bool expedition = CompanionExpeditionSystem.Owns(NPC.whoAmI), combat = CompanionCombatSystem.Owns(NPC.whoAmI);
        bool garden = CompanionGardenSystem.Owns(NPC.whoAmI), chain = CraftChainSystem.Owns(NPC.whoAmI);
        bool structure = StructureWorkshop.Owns(NPC.whoAmI), platform = CompanionConstructionSystem.Owns(NPC.whoAmI);
        if (!(expedition || combat || garden || chain || structure || platform)) return false;
        if (!_constructionDisplay) _followNavigation.Reset(NPC);
        _constructionDisplay = true;
        if (expedition) { CompanionExpeditionSystem.TickActive(this); UpdateStatus(CompanionExpeditionSystem.Status); }
        else if (combat) { CompanionCombatSystem.TickActive(this); UpdateStatus(CompanionCombatSystem.Status); }
        else if (garden) { if (!CompanionGardenSystem.Moving) MovementHelper.Hold(NPC); UpdateStatus(CompanionGardenSystem.Status); }
        else
        {
            if (!CompanionConstructionSystem.SupplyMoving && !CraftChainSystem.Moving && !StructureWorkshop.ArenaMoving) MovementHelper.Hold(NPC);
            UpdateStatus(chain ? (CraftChainSystem.Asking ? "合成链等待确认" : "执行合成链") : structure ? "建筑施工" : HasPendingApproval ? "等待采集确认" : "平台施工");
        }
        return true;
    }
    private void Follow()
    {
        var target = _commander is { active: true, dead: false } ? _commander :
            Main.player.Where(p => p != null && p.active && !p.dead).OrderBy(p => Vector2.DistanceSquared(p.Center, NPC.Center)).FirstOrDefault();
        if (target == null) { _followNavigation.Reset(NPC); return; }
        bool fighting = CompanionCombatSystem.TickAutomatic(this, target);
        if (fighting != _combatMovement) _followNavigation.Reset(NPC);
        _combatMovement = fighting;
        UpdateStatus(fighting ? "自动近身反击" : _followNavigation.Tick(NPC, target, 4));
    }
    public float GetJumpMultiplier() => 1;
    private void UpdateFacing()
    { if (Math.Abs(NPC.velocity.X) > .05f) NPC.direction = NPC.velocity.X < 0 ? -1 : 1; NPC.spriteDirection = NPC.direction; }
    private void SendChatMessage(string message, Color color) => CompanionMessages.Send($"[{NPC.GivenName}] {message}", color);
    private void UpdateStatus(string? message = null)
    {
        string text = message ?? State switch { AgentState.Planning => "正在规划", AgentState.Replanning => "重新规划",
            AgentState.Executing => "正在执行", AgentState.WaitingForApproval => "等你同意", AgentState.Completed => "这一步结束了",
            _ => Mode == CompanionMode.Wait ? "原地等待" : "" };
        if (_statusMessage == text) return;
        _statusMessage = text; NPC.netUpdate = true;
    }
    public void ReceiveCommand(Player? commander, string command, string? episodeId = null, string? sourceTurnId = null)
    {
        if (!ServerAuthority.IsServer || string.IsNullOrWhiteSpace(command) || command.Length > CompanionProtocol.MaxCommandLength) return;
        if (CompanionMemorySystem.Store == null) throw new InvalidOperationException("世界记忆尚未就绪。");
        StopCommand("superseded"); CompanionCommandService.Advance(); _behavior.Follow();
        _commander = commander; _currentCommand = command.Trim(); _sourceTurnId = sourceTurnId;
        _episodeId = episodeId ?? CompanionMemorySystem.Episodes!.Begin(sourceTurnId, command, CompanionObservation.Capture(commander, NPC));
        _taskId = Guid.NewGuid().ToString("N"); _taskClosed = false;
        _goal = TaskGoal.Parse(command); _goalDone = _decisionCount = _verifiedEffects = _planningRetryCount = 0;
        _skipTargets.Clear(); _programTarget = null; _oreWord = TaskGoal.OreWord(command); _anchor = TaskGoal.Anchor(command);
        _shortfall = _lastReport = _lastPlannerError = _lastGatherFailure = null; _reportDueTick = 0; _inventoryAtStart = SnapshotInventory(commander);
        CompanionTaskTracker.Begin(_taskId, _currentCommand);
        CompanionMemorySystem.Episodes!.Link(_episodeId, sourceTurnId, _taskId, command);
        CompanionMemorySystem.Record("command", _taskId, null, new { text = command, player = commander?.name });
        State = AgentState.Planning; BeginPlanning();
    }
    public void RecallToCommander(Player? commander)
    {
        if (!ServerAuthority.IsServer) return;
        StopCommand("recalled"); _behavior.Follow(); _commander = commander;
        if (commander is not { active: true, dead: false } || MovementHelper.FindValidTeleportPosition(commander) is not Vector2 point)
        { SendChatMessage("没有找到安全的召回位置。", Color.Orange); return; }
        NPC.Center = point; NPC.velocity = Vector2.Zero; NPC.netUpdate = true;
        SendChatMessage("回来了。", Color.LightGreen);
    }
    internal void ProcessPlanningWhilePaused()
    {
        if (State == AgentState.WaitingForApproval) ValidateApproval();
        if ((State is AgentState.Planning or AgentState.Replanning) && _plannerTask?.IsCompleted == true) TickPlanning();
    }
    internal void HoldActionTimersWhilePaused(long ticks)
    { if (State == AgentState.Executing && ticks > 0) { _executionStartTick += ticks; _actionStartTick += ticks; } }
    internal void WaitHere(Player commander)
    {
        _commander = commander; _followNavigation.Reset(NPC); _behavior.Wait(NPC.Center.X); MovementHelper.Hold(NPC); UpdateStatus("原地等待");
        CompanionMemorySystem.Record("companion_control", null, null, new { mode = "wait", revision = CompanionCommandService.Revision });
    }
    internal void ResumeFollowing(Player commander)
    {
        CompanionCombatSystem.ResumeAutomatic(); _commander = commander; _behavior.Follow(); _followNavigation.Reset(NPC); UpdateStatus("继续跟随");
        CompanionMemorySystem.Record("companion_control", null, null, new { mode = "follow", revision = CompanionCommandService.Revision });
    }
    private void ValidateApproval()
    {
        if (_pendingApproval?.Matches(CompanionMemorySystem.Session, _taskId, _pendingApproval.DecisionJson,
            DateTimeOffset.UtcNow, CompanionMemorySystem.Store?.Memory.RuleRevision ?? -1, Main.dayTime) == true) return;
        StopCommand("approval_expired"); SendChatMessage("确认已过期或规则已变化，请重新下达任务。", Color.Orange);
    }
    private void BeginPlanning()
    {
        if (_taskClosed || _taskId == null) { CancelPlanner(); return; }
        if (_plannerTask is { IsCompleted: false }) return;
        int limit = TerrarAI_Config.Get().MaxDecisionSteps;
        if (_goal.Countable) limit = Math.Max(limit, _goal.Target * 3);
        _programTarget = null;
        if (_goal.Countable)
        {
            if (ToolSelector.FindBestTool(_commander, _goal.Kind == "chop" ? ToolType.Axe : ToolType.Pickaxe) == null)
            { HandlePlannerFailure("背包缺少所需采集工具。"); return; }
            _programTarget = PickGatherTarget();
            if (_programTarget == null) { NoTargetLeft(); return; }
        }
        if (_decisionCount >= limit)
        {
            // Program-picked gather attempts are not model planning turns.
            // Preserve the same bound, but close through the normal shortfall
            // and recovery path with the latest execution cause and progress.
            if (_goal.Countable) NoTargetLeft($"已达到本次采集尝试上限（{limit} 次），仍有目标未核实；请走近露出的目标或开通道路后重试");
            else HandlePlannerFailure("达到规划步数上限，任务尚未核实完成。");
            return;
        }
        CompanionTaskTracker.Update(_taskId, _decisionCount == 0 ? CompanionTaskPhase.Planning : CompanionTaskPhase.Replanning);
        _planningStartTick = Main.GameUpdateCount; _decisionCount++;
        _plannerTask = RequestDecision();
    }
    private Task<string> RequestDecision()
    {
        CancelPlanner(); _plannerSession = CompanionMemorySystem.Session;
        _plannerCancellation = CancellationTokenSource.CreateLinkedTokenSource(CompanionMemorySystem.WorldToken);
        _decisionId = Guid.NewGuid().ToString("N"); _plannerTaskId = _taskId; _plannerDecisionId = _decisionId;
        _plannerControlStamp = CompanionCommandService.Capture(); _planningDay = Main.dayTime;
        _planningRevision = CompanionMemorySystem.Store?.Memory.RuleRevision ?? -1;
        _decisionStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_programTarget is Point target)
        {
            string decision = $"{{\"action\":{{\"type\":\"{_goal.Kind}\",\"params\":{{\"tileX\":{target.X},\"tileY\":{target.Y}}}}},\"mode\":\"act\"}}";
            CompanionMemorySystem.Record("decision_request", _taskId, _decisionId, new { origin = "program", decision, skipped = _skipTargets.Count,
                anchor = _anchor, ore = _oreWord, ruleRevision = _planningRevision, day = _planningDay });
            return Task.FromResult(decision);
        }
        string state = CompanionPrompt.Build(NPC, _commander, _currentCommand!, _replanContext, _lockedMineTarget);
        CompanionMemorySystem.Record("decision_request", _taskId, _decisionId, new { system = CompanionProtocol.SystemPrompt, state,
            model = TerrarAI_Config.Get().Model, ruleRevision = _planningRevision, day = _planningDay });
        var request = TerrarAI.RequireClient().SendChatCompletionAsync(CompanionProtocol.SystemPrompt, state, cancellationToken: _plannerCancellation.Token);
        _ = request.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return request;
    }
    private void TickPlanning()
    {
        if (_taskClosed) { CancelPlanner(); State = AgentState.Idle; return; }
        if (_plannerTask == null) { BeginPlanning(); return; }
        if (!_plannerTask.IsCompleted)
        {
            MovementHelper.Hold(NPC);
            if (Main.GameUpdateCount - _planningStartTick > TerrarAI_Config.Get().MaxPlanningSeconds * 60) HandlePlannerFailure("模型规划超时。");
            return;
        }
        if (_plannerSession != CompanionMemorySystem.Session || !_plannerControlStamp.Matches(CompanionMemorySystem.Session, CompanionCommandService.Revision) ||
            _plannerTaskId != _taskId || _plannerDecisionId != _decisionId)
        { CompanionMemorySystem.Record("decision_discarded", _plannerTaskId, _plannerDecisionId, new { reason = "stale_control" }); StopCommand("stale_control"); return; }
        try
        {
            string response = _plannerTask.GetAwaiter().GetResult(); _plannerTask = null;
            CompanionMemorySystem.Record("model_response", _taskId, _decisionId, new { output = response,
                latencyMs = System.Diagnostics.Stopwatch.GetElapsedTime(_decisionStartedAt).TotalMilliseconds });
            if (_planningDay != Main.dayTime || _planningRevision != CompanionMemorySystem.Store?.Memory.RuleRevision) { BeginPlanning(); return; }
            var actions = ActionParser.Parse(response, NPC, _commander, singleTileMining: _goal.Countable);
            var decision = CompanionProtocol.Parse(response);
            var store = CompanionMemorySystem.Store ?? throw new InvalidOperationException("世界记忆不可用。");
            var permission = CompanionPolicy.Check(store.Memory.Preferences, decision, day: Main.dayTime);
            CompanionTaskTracker.Update(_taskId, CompanionTaskPhase.Planning, decision.Type, decisionId: _decisionId);
            CompanionMemorySystem.Record("decision_measurement", _taskId, _decisionId, new { day = Main.dayTime, ruleRevision = _planningRevision,
                proposal = decision.Json, measurement = DecisionMeasurement.Measure(decision, permission) });
            if (permission == PolicyResult.Deny)
            { StopCommand("preference_forbids_action"); SendChatMessage("当前规则禁止这个动作，没有执行。", Color.Orange); return; }
            if (permission == PolicyResult.Ask || decision.Mode == "ask")
            {
                _pendingApproval = new(CompanionMemorySystem.Session, _taskId!, decision.Json, DateTimeOffset.UtcNow.AddMinutes(2), _planningRevision, Main.dayTime);
                State = AgentState.WaitingForApproval; CompanionTaskTracker.Update(_taskId, CompanionTaskPhase.WaitingForApproval);
                CompanionMemorySystem.Record("action_confirmation_requested", _taskId, _decisionId, new { action = decision.Json, origin = decision.Mode == "ask" ? "model" : "guard" });
                SendChatMessage($"想执行{decision.Type}，目标 tile({decision.Parameters.GetProperty("tileX")},{decision.Parameters.GetProperty("tileY")})。请同意或拒绝本次动作。", Color.LightBlue);
                return;
            }
            _executionDecision = decision; _executionApproved = false; _executionDay = Main.dayTime; _executionRevision = _planningRevision;
            _executionControlStamp = CompanionCommandService.Capture(); QueueActions(actions); _planningRetryCount = 0;
            State = AgentState.Executing; CompanionTaskTracker.Update(_taskId, CompanionTaskPhase.Ready);
            _executionStartTick = _actionStartTick = Main.GameUpdateCount;
            CompanionMemorySystem.Record("action_accepted", _taskId, _decisionId, new { output = response });
        }
        catch (Exception error)
        {
            CompanionMemorySystem.Record("action_rejected", _taskId, _decisionId, new { error = error.Message });
            CancelPlanner();
            if (_planningRetryCount++ == 0) { _replanContext = "规划被拒绝：" + error.Message; BeginPlanning(); }
            else HandlePlannerFailure(error.Message);
        }
    }
    private void QueueActions(IReadOnlyList<AgentAction> actions)
    { _actionQueue.Clear(); foreach (var action in actions) _actionQueue.Enqueue(action); }
    private void TickExecuting()
    {
        if (_taskClosed || !_executionControlStamp.Matches(CompanionMemorySystem.Session, CompanionCommandService.Revision))
        { StopCommand("stale_control"); return; }
        var store = CompanionMemorySystem.Store;
        if (_executionDecision == null || store == null ||
            _executionApproved && (_executionDay != Main.dayTime || _executionRevision != store.Memory.RuleRevision) ||
            CompanionPolicy.Check(store.Memory.Preferences, _executionDecision, _executionApproved, Main.dayTime) != PolicyResult.Allow)
        { StopCommand("preference_changed_during_execution"); SendChatMessage("规则变化，当前动作已停止。", Color.Orange); return; }
        if (_currentAction == null)
        {
            if (!_actionQueue.TryDequeue(out _currentAction)) { CloseTask("empty_queue", "没有待执行动作，任务未核实完成。"); State = AgentState.Completed; return; }
            _currentAction.Reset();
            _actionBefore = CompanionObservation.Capture(_commander, NPC, _currentAction.GetTargetTile(), _currentAction is MineAction ? 1 : 0);
            if (_currentAction is MineAction or ChopAction)
            {
                _lockedMineTarget = _currentAction.GetTargetTile();
                if (_currentAction is ChopAction && _lockedMineTarget is Point root && !TaskTargetClaims.Acquire(root, NPC.whoAmI))
                { ReplanAfterFailure("采集目标已被占用。"); return; }
            }
        }
        CompanionTaskTracker.Update(_taskId, CompanionTaskPhase.Executing);
        AgentAction action = _currentAction;
        var result = action.Tick(new(NPC, _commander));
        if (result.Status == AgentActionStatus.Pending) { UpdateStatus(result.Message); return; }
        var target = _executionDecision.Type is "mine" or "chop"
            ? new Point(_executionDecision.Parameters.GetProperty("tileX").GetInt32(), _executionDecision.Parameters.GetProperty("tileY").GetInt32()) : action.GetTargetTile();
        bool verified = target is Point point && action.Mutations.Any(m => m.X == point.X && m.Y == point.Y && m.Removed);
        bool removed = action.Mutations.Any(m => m.Removed);
        if (verified) _verifiedEffects++;
        if (result.Status == AgentActionStatus.Success && removed && (_goal.Kind == "chop" && action is ChopAction || _goal.Kind == "mine" && action is MineAction)) _goalDone++;
        else if (!removed) SkipTarget(target);
        string receipt = action is MineAction or ChopAction ? $"移除记录 {action.Mutations.Count(m => m.Removed)} 格；原始目标{(verified ? "已" : "未")}核实。"
            : action is MoveAction ? "移动执行器已返回结果。" : "口头结束不作为完成证明。";
        string progress = _goal.Countable ? $"；进度 {_goalDone}/{_goal.Target}，不要重复刚才的坐标。" : "";
        CompanionMemorySystem.Record("action_result", _taskId, _decisionId, new { action = action.Name, status = result.Status.ToString(), message = result.Message,
            attempt = 0, candidate = verified && result.Status == AgentActionStatus.Success, targetVerified = verified, mutations = action.Mutations,
            before = _actionBefore, target = target.HasValue ? new[] { target.Value.X, target.Value.Y } : null,
            after = CompanionObservation.Capture(_commander, NPC, target, action is MineAction ? 1 : 0),
            effectScope = "direct_target_tiles_only", inventoryAttribution = "unknown", at = new[] { (int)(NPC.Center.X / 16), (int)(NPC.Center.Y / 16) } });
        CompanionTaskTracker.Update(_taskId, CompanionTaskPhase.Executing, result: receipt + progress, verified: _verifiedEffects);
        CompanionMemorySystem.Observe($"{CompanionProtocol.Clip(_currentCommand, 40)}: {result.Status}; {receipt}{progress}");
        if (result.Status == AgentActionStatus.Failure) { ReplanAfterFailure(result.Message ?? "动作未成功。"); return; }
        CollectNearbyItems(); _autoCollectTicksRemaining = 120;
        bool ended = action is CompleteAction or SayAction;
        action.Cancel(); _currentAction = null; ClearTargetLock();
        if (ended) { CloseTask("model_stopped", result.Message); State = AgentState.Completed; return; }
        if (_goal.Countable && _goalDone >= _goal.Target) { FinishGoal(); return; }
        _actionQueue.Clear(); _replanContext = receipt + progress; State = AgentState.Replanning; BeginPlanning();
    }
    private void ReplanAfterFailure(string reason)
    {
        if (_goal.Countable) _lastGatherFailure = CompanionProtocol.Clip(reason, 180);
        SkipTarget(_currentAction?.GetTargetTile()); _currentAction?.Cancel(); _currentAction = null;
        _actionQueue.Clear(); ClearTargetLock(); _replanContext = "动作失败，已完成部分保留：" + reason;
        State = AgentState.Replanning; BeginPlanning();
    }
    private void HandlePlannerFailure(string error)
    {
        CompanionMemorySystem.Record("planner_error", _taskId, _decisionId, new { error });
        CompanionTaskTracker.Update(_taskId, CompanionTaskPhase.Failed, result: "规划未成功：" + CompanionProtocol.Clip(error, 160));
        CloseTask("planner_error", error); CancelPlanner(); ClearTargetLock();
        _lastPlannerError = error; State = AgentState.Completed;
        SendChatMessage("任务暂停：" + error, Color.OrangeRed);
    }
    private void ClearTargetLock() { TaskTargetClaims.Release(NPC.whoAmI); _lockedMineTarget = null; }
    private void CollectNearbyItems()
    { if (_commander is { active: true, dead: false }) CompanionDrops.Pickup(_commander, NPC); }
    private void TickAutoCollect()
    { if (_autoCollectTicksRemaining > 0) { _autoCollectTicksRemaining--; CollectNearbyItems(); } }

        private void CancelPlanner()
        {
            _plannerCancellation?.Cancel();
            _plannerCancellation?.Dispose();
            _plannerCancellation = null;
            _plannerTask = null;
            _plannerTaskId = _plannerDecisionId = null;
        }

        // Remember a target we could not finish so the picker moves on to the next one.
        private void SkipTarget(Point? tile)
        {
            if (!_goal.Countable) return;
            if (tile is Point t) _skipTargets.Add(t);
            if (_programTarget is Point p) _skipTargets.Add(p);
            if (_lockedMineTarget is Point locked) { _skipTargets.Add(locked); ClearTargetLock(); }
        }

        private Point? PickGatherTarget()
        {
            var (center, side) = GatherTargets.Origin(NPC, _commander, _anchor);
            if (_goal.Kind == "chop") return GatherTargets.NextTree(NPC, center, side, _skipTargets);
            int pick = ToolSelector.FindBestTool(_commander, ToolType.Pickaxe)?.pick ?? 0;
            return GatherTargets.NextOre(NPC, center, side, _skipTargets, pick, _oreWord);
        }

        // Nothing reachable is left: report honestly instead of asking the model to guess.
        internal string RecoveryKind(string taskId)=>taskId==_taskId&&_goal.Countable?"gather":"";
        internal string RecoveryProgress=>$"已核实{_goal.Verb}{_goalDone}/{_goal.Target}{_goal.Unit}，动作效果{_verifiedEffects}次；这是现场移除量，不等于掉落已入包。";

        private void NoTargetLeft(string? obstacle = null)
        {
            int pick = ToolSelector.FindBestTool(_commander, ToolType.Pickaxe)?.pick ?? 0;
            string what = _goal.Kind == "chop" ? "能走过去的树"
                : _oreWord == null ? "能挖的矿"
                : _oreWord + (_oreWord is "陨石" or "狱石" or "黑曜石" || _oreWord.EndsWith("矿") || TaskGoal.IsBlock(_oreWord) ? "" : "矿");
            string where = _anchor == "companion" ? "附近" : "你说的位置附近";
            string reason = obstacle ?? (_goal.Kind == "mine" && GatherTargets.TooHardFor(_oreWord, pick)
                ? $"挖{what}需要更好的镐（现在镐力 {pick}%）"
                : $"{where}没找到{(_skipTargets.Count > 0 ? "别的" : "")}{(_goal.Kind == "mine" ? "露出且镐力足够的" : "")}{what}");
            if (_lastGatherFailure != null) reason += "；最近一次失败：" + _lastGatherFailure;
            if (_goal.Kind == "mine" && obstacle == null) reason += "；埋住的矿可说「规划通道挖矿」，先预览并确认再开路";
            CompanionMemorySystem.Record("gather_shortfall", _taskId, _decisionId, new { reason, attempts = _decisionCount,
                skipped = _skipTargets.Count, done = _goalDone, target = _goal.Target, lastFailure = _lastGatherFailure,
                outcome = obstacle == null ? "no_candidate" : "attempt_limit" });
            if (_goalDone > 0) { FinishGoal(reason); return; }
            // Closing publishes the failure receipt immediately. Set the cause
            // first so recovery sees the same final result as the chat/UI.
            CompanionTaskTracker.SetResult(_taskId, reason + "。");
            CloseTask("no_target", reason);
            CancelPlanner();
            _actionQueue.Clear();
            State = AgentState.Completed;
            UpdateStatus("采集未完成");
            SendChatMessage(reason + (_skipTargets.Count > 0 ? "（够不着的我先跳过了）" : "") + "。换个地方再叫我吧。", Color.LightBlue);
        }

        private void FinishGoal(string? shortfall = null)
        {
            _shortfall = shortfall;
            string summary = $"{_goal.Verb}了 {_goalDone} {_goal.Unit}" + (shortfall == null ? "。" : $"；{shortfall}。");
            if(shortfall==null)CompanionTaskTracker.Finish(_taskId, summary, _verifiedEffects);
            else { CompanionTaskTracker.SetResult(_taskId,summary);CompanionTaskTracker.Close(_taskId,"no_more_targets",_verifiedEffects); }
            CompanionMemorySystem.Record("command_closed", _taskId, _decisionId,
                new { reason = shortfall == null ? "goal_reached" : "no_more_targets", message = summary, goalVerified = shortfall == null, goal = _goal.Kind,
                    target = _goal.Target, done = _goalDone, verifiedActionEffects = _verifiedEffects });
            if (_episodeId != null) CompanionMemorySystem.Episodes?.Close(_episodeId, shortfall == null ? "goal_reached" : "no_more_targets");
            _taskClosed = true;
            _reportTaskId = _taskId;
            _reportDueTick = Main.GameUpdateCount + 90; // drops are picked up over the next second or so
            CancelPlanner();
            _actionQueue.Clear();
            ClearTargetLock();


            State = AgentState.Completed;
            UpdateStatus("完成");
        }

        private void TickReport()
        {
            if (_reportDueTick == 0 || Main.GameUpdateCount < _reportDueTick) return;
            _reportDueTick = 0;
            string gains = DescribeGains();
            string text = _goal.Kind == "chop" ? $"砍好了！一共砍了 {_goalDone} 棵树" : $"挖好了！一共挖了 {_goalDone} 处";
            text += gains.Length > 0 ? $"，拿到 {gains}。" : "。东西可能还掉在地上，记得捡一下。";
            if (_shortfall != null) text += $"只做到这些：{_shortfall}。";
            _lastReport = text;
            CompanionTaskTracker.SetResult(_reportTaskId, text);
            CompanionMemorySystem.Record("task_report", _reportTaskId, null, new { text, gains });
            CompanionMemorySystem.Observe(text);
            SendChatMessage(text, Color.LightGreen);
        }

        private static Dictionary<int, int> SnapshotInventory(Player? player)
        {
            var counts = new Dictionary<int, int>();
            if (player == null) return counts;
            for (int i = 0; i < 58 && i < player.inventory.Length; i++)
            {
                var item = player.inventory[i];
                if (item == null || item.IsAir) continue;
                counts[item.type] = counts.GetValueOrDefault(item.type) + item.stack;
            }
            return counts;
        }

        private string DescribeGains()
        {
            if (_commander == null || _inventoryAtStart == null) return "";
            var before = _inventoryAtStart;
            var gained = SnapshotInventory(_commander)
                .Select(p => (type: p.Key, count: p.Value - before.GetValueOrDefault(p.Key)))
                .Where(p => p.count > 0).OrderByDescending(p => p.count).Take(4)
                .Select(p => $"{Lang.GetItemNameValue(p.type)}×{p.count}");
            return string.Join("、", gained);
        }

        // Answers "砍完了吗" from what actually happened; never starts or stops anything.
        internal string DescribeStatus()
        {
            if (CompanionCollaborationSystem.Current is {} collaboration) return collaboration.Describe();
            if (CompanionExpeditionSystem.IsTask(CompanionTaskTracker.Current?.TaskId??"")) return CompanionExpeditionSystem.Describe();
            if (CompanionCombatSystem.Owns(NPC.whoAmI)) return CompanionCombatSystem.Describe();
            if (CompanionCombatSystem.Fighting) return CompanionCombatSystem.AutomaticDescription();
            if (CompanionGardenSystem.Owns(NPC.whoAmI)) return CompanionGardenSystem.Describe();
            if (CraftChainSystem.Owns(NPC.whoAmI)) return CraftChainSystem.Describe();
            if (CompanionConstructionSystem.Owns(NPC.whoAmI) && CompanionConstructionSystem.Supply != null)
                return CompanionConstructionSystem.SupplyDescription() + "。" + CompanionConstructionSystem.Notice;
            if (CompanionConstructionSystem.Owns(NPC.whoAmI)) return "在搭平台呢。";
            if (StructureWorkshop.Owns(NPC.whoAmI)) return $"在盖「{StructureWorkshop.Plan?.Name}」呢，{StructureWorkshop.Notice}";
            if (_reportDueTick != 0) return $"刚{_goal.Verb}完，正在捡掉落的东西。";
            if (!_taskClosed && _taskId != null)
            {
                if (State == AgentState.WaitingForApproval) return "在等你点「同意」呢。";
                if (_goal.Countable)
                    return $"还在{_goal.Verb}，已经{_goal.Verb}了 {_goalDone}/{_goal.Target} {_goal.Unit}。";
                return "还在做：" + CompanionProtocol.Clip(_currentCommand, 30);
            }
            if (_lastReport != null) return "刚才那个活：" + _lastReport;
            var last = CompanionTaskTracker.Current;
            if (last != null && last.Closed && last.Reason.Length > 0) return "上一个活没做完：" + last.Reason;
            return Mode == CompanionMode.Wait ? "没在干活，在原地等你。" : "没在干活，跟着你呢。";
        }

        private void CloseTask(string reason, string? message)
        {
            if (_taskClosed) return;
            CompanionTaskTracker.Close(_taskId, reason, _verifiedEffects);
            CompanionMemorySystem.Record("command_closed", _taskId, _decisionId,
                new { reason, message, goalVerified = false, verifiedActionEffects = _verifiedEffects });
            if (_episodeId != null) CompanionMemorySystem.Episodes?.Close(_episodeId, reason);
            _taskClosed = true;
        }

        public void StopCommand(string reason)
        {
            _followNavigation.Reset(NPC);
            if (_currentAction != null)
            {
                CompanionMemorySystem.Record("action_interrupted", _taskId, _decisionId,
                    new { reason, action = _currentAction.Name, mutations = _currentAction.Mutations });
                CompanionTaskTracker.Update(_taskId, CompanionTaskPhase.Stopped,
                    result: $"动作已中断，已记录 {_currentAction.Mutations.Count(m => m.Removed)} 个直接移除的方块；整项目标未核验。");
            }
            CloseTask(reason, "Stopped before further actions.");
            CancelPlanner();
            _actionQueue.Clear();
            _currentAction?.Cancel();
            _currentAction = null;
            _currentCommand = null;
            _episodeId = _sourceTurnId = null;
            _pendingApproval = null;
            _executionDecision = null;
            _executionApproved = false;
            _replanContext = null;
            _autoCollectTicksRemaining = 0;
            _taskId = _decisionId = null;
            TaskTargetClaims.Release(NPC.whoAmI);

            ClearTargetLock();


            State = AgentState.Idle;
            UpdateStatus();
        }

        public string ConfirmPendingAction(Player player)
        {
            if (CompanionExpeditionSystem.Asking) return CompanionExpeditionSystem.Confirm(player, CompanionExpeditionSystem.ConfirmationId);
            if (CompanionGardenSystem.Asking) return CompanionGardenSystem.Confirm(player, CompanionGardenSystem.ConfirmationId);
            if (StructureWorkshop.ArenaAsking) return StructureWorkshop.Confirm(player, StructureWorkshop.ArenaConfirmationId);
            if (CraftChainSystem.Asking) return CraftChainSystem.Confirm(player, CraftChainSystem.ConfirmationId);
            if (CompanionConstructionSystem.SupplyAsking) return CompanionConstructionSystem.ConfirmSupplyHarvest(player);
            var ticket = _pendingApproval;
            if (ticket == null || !ticket.Matches(CompanionMemorySystem.Session, _taskId, ticket.DecisionJson, DateTimeOffset.UtcNow,
                CompanionMemorySystem.Store?.Memory.RuleRevision ?? -1, Main.dayTime))
            {
                if (ticket != null) StopCommand("approval_expired_or_world_changed");
                return "当前没有等待确认的动作。";
            }
            if (_commander?.whoAmI != player.whoAmI) return "只有发起任务的玩家可以确认。";
            try
            {
                var decision = CompanionProtocol.Parse(ticket.DecisionJson);
                var store = CompanionMemorySystem.Store ?? throw new InvalidOperationException("World memory unavailable.");
                if (CompanionPolicy.Check(store.Memory.Preferences, decision, approved: true, day: Main.dayTime) != PolicyResult.Allow)
                    throw new InvalidOperationException("当前偏好仍禁止这个动作，请先修改偏好。");
                // Revalidate the exact original target/tools. Confirmation never asks the model for a different action.
                var actions = ActionParser.Parse(ticket.DecisionJson, NPC, player, singleTileMining: _goal.Countable);
                CompanionMemorySystem.Record("action_confirmed", _taskId, _decisionId, new { action = ticket.DecisionJson });
                _pendingApproval = null;
                _executionDecision = decision;
                _executionApproved = true;
                _executionDay = Main.dayTime;
                _executionRevision = store.Memory.RuleRevision;
                _executionControlStamp = CompanionCommandService.Capture();
                QueueActions(actions);
                State = AgentState.Executing;
                CompanionTaskTracker.Update(_taskId, CompanionTaskPhase.Ready);
                _executionStartTick = _actionStartTick = Main.GameUpdateCount;

                UpdateStatus();
                return "收到，只执行这次确认的动作。";
            }
            catch (Exception ex)
            {
                StopCommand("confirmation_rejected");
                return "没有执行：" + ex.Message;
            }
        }

}
