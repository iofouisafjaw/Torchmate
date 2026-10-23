using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using TerrarAI.Content.Actions;
using TerrarAI.Content.Companion;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrarAI.Content.Systems;

public sealed partial class CompanionConstructionSystem
{
    internal static PlatformSupplyJob? Supply { get; private set; }
    private static TerrariaPlatformSupplySite? _supplySite;
    internal static bool SupplyMoving => Supply?.Stage == SupplyStage.Gathering && _supplySite?.Moving == true;
    internal static bool SupplyAsking => Active && _supplySite?.Asking == true;
    internal static bool CanSupply(Player player) => Main.netMode == NetmodeID.SinglePlayer && Phase == ConstructionPhase.Preview
        && Blueprint is { Conflicts: 0 } && _stamp.Matches(CompanionMemorySystem.Session, CompanionCommandService.Revision)
        && player.whoAmI == _player && player.active && !player.dead && ChatCoordinator.FindCompanion()?.NPC.whoAmI == _agent
        && Crafting.PlatformRecipe() != null;
    internal static string ConfirmSupply(Player player, Guid previewId)
    {
        if (previewId == Guid.Empty || previewId != PreviewId || !CanSupply(player))
            return "蓝图、配方或任务已变化，请查看最新预览。";
        var blueprint = Blueprint!;
        if (blueprint.Slots.Any(s => Inspect(s.At) != s.Before))
        { Preview(blueprint.Region); return "现场已变化，请重新确认。"; }
        var recipe = Crafting.PlatformRecipe()!;
        var agent = ChatCoordinator.FindCompanion()!;
        _supplySite = new(player, agent.NPC, recipe, _task!, _episode);
        Supply = new(blueprint, recipe.requiredItem.Single(i => !i.IsAir).stack, recipe.createItem.stack);
        Job = Supply.Build; Phase = ConstructionPhase.Running; _ticks = 0;
        Notice = "已确认备料→合成→施工，开始核对材料。缺少普通木材时只在确认时同伴附近 28 格内采集。";
        CompanionTaskTracker.Update(_task, CompanionTaskPhase.Ready, "定量备料→合成木平台→施工核验", result: Notice);
        CompanionMemorySystem.Record("construction_supply_confirmed", _task, null,
            new { previewId, budget = Supply.Budget(_supplySite), center = _supplySite.Center,
                centerX = _supplySite.Center.X, centerY = _supplySite.Center.Y,
                radius = 28, recipe = recipe.RecipeIndex, input = Supply.Input, output = Supply.Output }, _episode);
        Revision++; return Notice;
    }
    internal static string ConfirmSupplyHarvest(Player player) =>
        _supplySite != null && player.whoAmI == _player ? _supplySite.Approve() : "当前没有等待确认的备料动作。";

    private static void TickSupply()
    {
        var supply = Supply!; var site = _supplySite!;
        // Gather at game speed; keep the existing one-cell-per-four-ticks placement rate.
        if (supply.Stage == SupplyStage.Building && ++_ticks % 4 != 0) return;
        var previous = supply.Stage; int placed = supply.Build.Placed;
        try { supply.Step(site); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { supply.Abort(site, "备料施工异常，保留已完成的变化：" + ex.GetType().Name); }
        if (supply.Build.Placed != placed)
        {
            var at = supply.Build.Blueprint.Slots[supply.Build.Cursor - 1].At;
            CompanionMemorySystem.Record("construction_placement", _task, null,
                new { at.X, at.Y, consumed = 1, placed = supply.Build.Placed }, _episode);
        }
        Notice = supply.Stage == SupplyStage.Building ? $"施工 {Job!.Cursor}/{Job.Blueprint.Slots.Count} · 新放 {Job.Placed}" : supply.Reason;
        if (Notice.Length == 0) Notice = "核对背包与备料缺口……";
        CompanionTaskTracker.Update(_task, site.Asking ? CompanionTaskPhase.WaitingForApproval : CompanionTaskPhase.Executing,
            action: SupplyStageText(supply.Stage), target: site.TargetText, result: Notice);
        if (previous != supply.Stage)
            CompanionMemorySystem.Record("construction_supply_stage", _task, null,
                new { before = previous.ToString(), after = supply.Stage.ToString(), supply.Crafted, supply.WoodConsumed }, _episode);
        Revision++;
        if (supply.Stage is not (SupplyStage.Completed or SupplyStage.Failed)) return;
        site.CancelHarvest();
        Phase = supply.Stage == SupplyStage.Completed ? ConstructionPhase.Completed : ConstructionPhase.Failed;
        Notice = supply.Reason + $" 入包普通木材 {site.Collected}，合成消耗 {supply.WoodConsumed}，产出木平台 {supply.Crafted}，新放 {Job!.Placed}，复用 {Job.Reused}。";
        var audit = Job.Audit ?? Job.Blueprint.Audit(Inspect);
        CompanionMemorySystem.Record("construction_audit", _task, null,
            new { audit.Passed, audit.Required, audit.Present, audit.Missing, placed = Job.Placed, supply.Crafted, supply.WoodConsumed,
                collectedWood = site.Collected, reason = supply.Reason }, _episode);
        if (supply.Stage == SupplyStage.Completed) CompanionTaskTracker.CompleteVerified(_task, Notice);
        else { CompanionTaskTracker.SetResult(_task, Notice); CompanionTaskTracker.Close(_task, "construction_failed", 0); }
        CloseEpisode(supply.Stage == SupplyStage.Completed ? "construction_verified" : "construction_failed");
        CompanionUISystem.Message(Notice, supply.Stage == SupplyStage.Completed ? Color.LightGreen : Color.Orange);
    }
    internal static string SupplyDescription()
    {
        if (Supply != null && _supplySite != null)
        {
            var b = Supply.Stage is SupplyStage.Gathering or SupplyStage.Crafting ? Supply.Budget(_supplySite) : null;
            return $"备料链：{SupplyStageText(Supply.Stage)} · 入包木材 {_supplySite.Collected} · 合成 {Supply.Crafted} 个平台" +
                (b == null ? "" : $" · 尚缺木材 {b.WoodShortfall}");
        }
        if (Blueprint == null || Crafting.PlatformRecipe() is not { } r) return "";
        var p = Main.player[_player];
        var budget = PlatformMaterialBudget.Calculate(Blueprint.Needed, Stock(p), TerrariaPlatformSupplySite.CountWood(p),
            r.requiredItem.Single(i => !i.IsAir).stack, r.createItem.stack);
        return $"合成 {budget.Output} 平台 · 需木材 {budget.WoodNeeded} · 尚缺 {budget.WoodShortfall}";
    }
    private static string SupplyStageText(SupplyStage stage) => stage switch {
        SupplyStage.Gathering => "采集普通木材", SupplyStage.Crafting => "合成木平台", SupplyStage.Building => "铺设并核验",
        SupplyStage.Completed => "施工达标", SupplyStage.Cancelled => "已取消", _ => "未完成" };
}

internal sealed class TerrariaPlatformSupplySite(Player player, NPC agent, Recipe recipe, string task, string? episode) : IPlatformSupplySite
{
    private readonly TerrariaPlatformSite _site = new(player);
    private readonly int _input = recipe.requiredItem.Single(i => !i.IsAir).stack, _output = recipe.createItem.stack;
    internal Point Center { get; } = agent.Bottom.ToTileCoordinates();
    private readonly HashSet<Point> _skip = new();
    private ChopAction? _chop;
    private CompanionDecision? _decision;
    private ApprovalTicket? _approval;
    private bool _approved;
    private long _rule;
    private bool _day;
    private int _settle, _attempts;
    private string? _lastHarvestFailure;
    internal int Collected { get; private set; }
    internal bool Asking => _approval != null;
    internal bool Moving => _chop != null;
    internal string TargetText => _decision == null ? "" : $"tile({_decision.Parameters.GetProperty("tileX")},{_decision.Parameters.GetProperty("tileY")})";
    internal static int CountWood(Player p) => p.inventory.Take(50).Where(i => i.type == ItemID.Wood).Sum(i => i.stack);
    public int Wood => CountWood(player);
    public int Available => CompanionConstructionSystem.Stock(player);
    public PlatformCell Inspect(BuildPoint p) => _site.Inspect(p);
    public bool TryPlace(BuildPoint p, out string error) => _site.TryPlace(p, out error);

    private bool SafeTree(Point p) => GatherTargets.SafeForestTree(p,Center,(x,y)=> {
        var r=CompanionConstructionSystem.Region!.Value;
        return x>=r.Left&&x<=r.Right&&y>=r.Top&&y<=r.Bottom;
    });
    public HarvestStep Harvest(out string message)
    {
        message = "正在备料……";
        var store = CompanionMemorySystem.Store;
        if (store == null) { message = "世界记忆不可用。"; return HarvestStep.Failed; }
        if (_approval != null && !_approval.Matches(CompanionMemorySystem.Session, task, _decision!.Json, DateTimeOffset.UtcNow,
            store.Memory.RuleRevision, Main.dayTime))
        { message = "采集许可过期、昼夜或规则变化，请重新确认任务。"; return HarvestStep.Failed; }
        if (_approval != null) { message = "备料需砍树 " + TargetText + "；请点工坊中的同意本次砍树，或输入 confirm。"; return HarvestStep.WaitingForApproval; }
        if (_chop != null && (_rule != store.Memory.RuleRevision || _day != Main.dayTime ||
            CompanionPolicy.Check(store.Memory.Preferences, _decision!, _approved, Main.dayTime) != PolicyResult.Allow))
        { message = "采集期间规则或昼夜变化，已停止。"; return HarvestStep.Failed; }
        if (_settle > 0)
        {
            CollectWood(); agent.velocity.X = 0;
            if (--_settle == 0) { _decision = null; return HarvestStep.Finished; }
            message = "正在拾取木材，以实际进入背包的数量核对缺口。"; return HarvestStep.Pending;
        }
        if (_chop == null)
        {
            if (++_attempts > 24) { message = "已达到 24 棵树的备料尝试上限。"; return HarvestStep.Failed; }
            if (ToolSelector.FindInventoryTool(player, ToolType.Axe) == null) { message = "缺少斧头，无法补齐木材。"; return HarvestStep.Failed; }
            if (!CanAcceptWood()) { message = "主背包没有木材空间，未继续砍树。"; return HarvestStep.Failed; }
            Point? target;
            while ((target = GatherTargets.NextTree(agent, Center, 0, _skip)) is Point t && !SafeTree(t)) _skip.Add(t);
            if (target is not Point at)
            {
                message = _lastHarvestFailure == null ? "确认范围内没有可采集的普通树，木材未备齐。"
                    : "备料未完成：" + _lastHarvestFailure;
                return HarvestStep.Failed;
            }
            _skip.Add(at);
            _decision = CompanionProtocol.Parse($"{{\"action\":{{\"type\":\"chop\",\"params\":{{\"tileX\":{at.X},\"tileY\":{at.Y}}}}},\"mode\":\"act\"}}");
            var permission = CompanionPolicy.Check(store.Memory.Preferences, _decision, day: Main.dayTime);
            if (permission == PolicyResult.Deny) { message = "当前偏好禁止砍树，木材未备齐。"; return HarvestStep.Failed; }
            _rule = store.Memory.RuleRevision; _day = Main.dayTime; _approved = false;
            CompanionMemorySystem.Record("construction_supply_target", task, null, new { at.X, at.Y, policy = permission.ToString(), origin = "program" }, episode);
            if (permission == PolicyResult.Ask)
            {
                _approval = new(CompanionMemorySystem.Session, task, _decision.Json, DateTimeOffset.UtcNow.AddMinutes(2), _rule, _day);
                message = "备料需砍树 " + TargetText + "，等待你同意本次动作。";
                CompanionUISystem.Message(message, Color.LightBlue); return HarvestStep.WaitingForApproval;
            }
            _chop = new(at, inventoryToolsOnly: true);
        }
        var point = _chop.GetTargetTile()!.Value;
        if (!SafeTree(point)) { message = "目标树或保护范围已变化，未继续采集。"; return HarvestStep.Failed; }
        var result = _chop.Tick(new(agent, player));
        message = result.Message ?? "正在采集普通木材……";
        if (result.Status == AgentActionStatus.Pending) return HarvestStep.Pending;
        CompanionMemorySystem.Record("construction_supply_harvest", task, null,
            new { target = TargetText, status = result.Status.ToString(), mutations = _chop.Mutations, message }, episode);
        if (result.Status == AgentActionStatus.Failure) _lastHarvestFailure = result.Message;
        _chop.Cancel(); _chop = null; agent.velocity.X = 0;
        if (result.Status == AgentActionStatus.Failure) { _decision = null; return HarvestStep.Finished; }
        // Drops can take a moment to reach the pickup radius; do not count a removed trunk as wood.
        _settle = 90;
        return HarvestStep.Pending;
    }
    internal string Approve()
    {
        var store = CompanionMemorySystem.Store;
        if (_approval == null || store == null || !_approval.Matches(CompanionMemorySystem.Session, task, _decision!.Json,
            DateTimeOffset.UtcNow, store.Memory.RuleRevision, Main.dayTime)) return "许可已失效，请停止后重新确认蓝图。";
        if (CompanionPolicy.Check(store.Memory.Preferences, _decision!, true, Main.dayTime) != PolicyResult.Allow) return "当前规则禁止砍树。";
        var p = new Point(_decision!.Parameters.GetProperty("tileX").GetInt32(), _decision.Parameters.GetProperty("tileY").GetInt32());
        if (!SafeTree(p)) return "目标已变化，没有执行。";
        _approved = true; _approval = null; _chop = new(p, inventoryToolsOnly: true);
        CompanionMemorySystem.Record("construction_supply_approved", task, null, new { target = TargetText, _rule, _day }, episode);
        return "已同意这棵树，继续采集。";
    }
    private bool CanAcceptWood() => player.inventory.Take(50).Any(i => i.IsAir || i.type == ItemID.Wood && i.stack < i.maxStack);
    private void CollectWood()
    {
        for (int i = 0; i < Main.maxItems; i++)
        {
            var item = Main.item[i];
            if (!item.active || item.type != ItemID.Wood || Vector2.Distance(item.Center, agent.Center) > 96 || !CanAcceptWood()) continue;
            int before = Wood; var position = item.position;
            var rest = player.GetItem(player.whoAmI, item, GetItemSettings.InventoryEntityToPlayerInventorySettings);
            Main.item[i] = rest; rest.position = position; rest.active = !rest.IsAir && rest.stack > 0;
            int gained = Wood - before; Collected += Math.Max(0, gained);
            if (gained > 0) CompanionMemorySystem.Record("construction_supply_pickup", task, null, new { item = ItemID.Wood, gained, wood = Wood }, episode);
        }
    }
    public bool Craft(int batches, out string message)
    {
        message = "确认后的配方数量发生变化，没有扣料。";
        if (recipe.createItem.stack != _output || recipe.requiredItem.Count(i => !i.IsAir) != 1 ||
            recipe.requiredItem.Single(i => !i.IsAir).stack != _input) return false;
        bool done = Crafting.MakePlatforms(player, recipe, batches, out message);
        CompanionMemorySystem.Record("construction_supply_craft", task, null, new { batches, done, message, wood = Wood, platforms = Available }, episode);
        return done;
    }
    public void CancelHarvest()
    { _chop?.Cancel(); _chop = null; _approval = null; _settle = 0; agent.velocity.X = 0; }
}
