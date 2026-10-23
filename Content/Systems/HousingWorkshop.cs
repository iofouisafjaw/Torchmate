using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using TerrarAI.Content.Companion;
using Terraria;
using Terraria.ID;

namespace TerrarAI.Content.Systems;

public sealed partial class StructureWorkshop
{
    internal static bool HousingMode { get; private set; }
    internal static HousingInspection? Housing { get; private set; }
    internal static HousingMaterialBudget? RepairBudget { get; private set; }
    private static readonly Queue<RepairCraft> _repairCrafts = new();
    private static int _repairCrafted, _repairBenchPlaced;
    private static string _confirmedBudget = "";

    private static void ResetHousing()
    { HousingMode = false; Housing = null; RepairBudget = null; _repairCrafts.Clear(); _repairCrafted = _repairBenchPlaced = 0; _confirmedBudget = ""; }

    internal static string BeginHousing(Player player)
    {
        if (Main.netMode != NetmodeID.SinglePlayer || !player.active || player.dead || CompanionMemorySystem.Session == Guid.Empty)
            return "请在单人世界、角色存活时检测房屋。";
        var agent = ChatCoordinator.FindCompanion();
        if (agent == null) return "请先唤来同伴。";
        CompanionCommandService.Advance(); CompanionAgent.Cancel(); CompanionConstructionSystem.Hide();
        CompanionDialogueSystem.Cancel(); CompanionInquirySystem.Cancel(); agent.StopCommand("housing_requested");
        ResetHousing(); ResetArena(); HousingMode = true;
        _stamp = CompanionCommandService.Capture(); _player = player.whoAmI; _agent = agent.NPC.whoAmI;
        _task = Guid.NewGuid().ToString("N");
        _episode = CompanionMemorySystem.Episodes?.Begin(null, "检测并修补房屋", new { player = player.name });
        if (_episode != null) CompanionMemorySystem.Episodes?.Link(_episode, null, _task, "检测并修补房屋");
        Plan = null; Origin = null; States = null; Missing = new(); Job = null; PreviewId = Guid.Empty;
        _previousLeft = false; _ready = false; Visible = true; Phase = StructurePhase.Placing;
        Notice = "点击房屋内部的空格开始检测；右键或 Esc 取消。检测和预览不改地形、不扣材料。";
        CompanionTaskTracker.Begin(_task, "房屋检测→确认修补→再次检测");
        Revision++; return Notice;
    }

    internal static void PreviewHousing(Point seed)
    {
        if (!HousingMode || Phase == StructurePhase.Running) return;
        PreviewId = Guid.NewGuid(); Job = null; RepairBudget = null;
        try
        {
            var player = Main.player[_player]; Housing = HousingInspector.Scan(player, seed);
            Plan = Housing.Plan; Origin = Housing.Bounds.Location;
            var site = RepairSite(player); States = Plan.Steps.Select(site.Inspect).ToArray();
            RepairBudget = RepairCrafting.Budget(player, Plan); Missing = RepairBudget.Missing;
            _confirmedBudget = BudgetStamp(RepairBudget);
            Phase = Housing.Suitable ? StructurePhase.Completed : StructurePhase.Preview;
            Notice = Housing.Suitable ? Housing.Summary : Plan.Steps.Count == 0 ? "只显示检测结果；请先手动处理结构或占用问题。" :
                !RepairBudget.CanPrepare ? "材料不足：" + MissingText(5) + "；补齐后点再次检测。" :
                States.Any(s => s == CellState.Blocked) ? "修补位置有受保护的内容，请重新检测。" :
                !StationsReady(player) ? "请带齐成品，或站到屋内工作台旁重新检测。" :
                "可修补内容已标蓝；确认后才备料和放置，完成后重新做原生检测。";
            CompanionTaskTracker.Update(_task, CompanionTaskPhase.WaitingForApproval, "修补现有房屋",
                $"tile({seed.X},{seed.Y})", DescribeHousing());
            if (Housing.Suitable)
            { CompanionTaskTracker.CompleteVerified(_task, Housing.Summary); CloseEpisode("housing_inspected_qualified"); }
            CompanionMemorySystem.Record("housing_inspected", _task, null, new
            {
                seed.X,
                seed.Y,
                Housing.Suitable,
                Housing.Issues,
                bounds = Housing.Bounds,
                steps = Plan.Steps.Count,
                missing = Missing
            }, _episode);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Housing = null; Plan = null; Origin = null; States = null; Missing = new(); PreviewId = Guid.Empty; Phase = StructurePhase.Placing;
            Notice = "检测未完成：" + ex.Message + " 请重新点击屋内空格。";
        }
        Revision++;
    }

    internal static void RecheckHousing()
    {
        if (!HousingMode || Housing == null || Phase == StructurePhase.Running) return;
        var seed = Housing.Seed;
        if (Phase is StructurePhase.Completed or StructurePhase.Failed or StructurePhase.Cancelled)
            BeginHousing(Main.player[_player]);
        PreviewHousing(seed);
    }

    private static string BudgetStamp(HousingMaterialBudget budget) =>
        string.Join(";", budget.Crafts.Select(c => $"{c.Recipe.Item}:{c.Batches}:{c.Recipe.Output}:{c.Recipe.Workbench}:" +
            string.Join(',', c.Recipe.Ingredients.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}")))) + "/" +
        string.Join(',', budget.Missing.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}"));

    private static bool StationsReady(Player player)
    {
        player.AdjTiles();
        return RepairBudget?.Crafts.All(c => !c.Recipe.Workbench || player.adjTile[TileID.WorkBenches] ||
            Plan!.Steps.Any(s => s.Item == ItemID.WorkBench)) == true;
    }

    internal static bool CanRepair(Player player) => HousingMode && Phase == StructurePhase.Preview && Housing != null && Plan?.Steps.Count > 0
        && States != null && States.All(s => s is CellState.Empty or CellState.Done) && RepairBudget?.CanPrepare == true
        && StationsReady(player) && Main.netMode == NetmodeID.SinglePlayer && player.whoAmI == _player && player.active && !player.dead
        && _stamp.Matches(CompanionMemorySystem.Session, CompanionCommandService.Revision) && ChatCoordinator.FindCompanion()?.NPC.whoAmI == _agent;

    private static string ConfirmHousing(Player player, Guid previewId)
    {
        if (previewId == Guid.Empty || previewId != PreviewId || !CanRepair(player)) return "预览已变化、材料不足或任务已取消，请再次检测。";
        HousingInspection live;
        try { live = HousingInspector.Scan(player, Housing!.Seed); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return "检测条件已变化：" + ex.Message; }
        var budget = RepairCrafting.Budget(player, Plan!);
        if (live.Fingerprint != Housing!.Fingerprint || BudgetStamp(budget) != _confirmedBudget ||
            Plan!.Steps.Select(RepairSite(player).Inspect).Where((s, i) => s != States![i]).Any())
        { PreviewHousing(Housing.Seed); return "房屋、背包或配方已变化，已刷新预览，请重新确认。"; }
        _repairCrafts.Clear(); foreach (var craft in budget.Crafts) _repairCrafts.Enqueue(craft);
        _repairCrafted = _repairBenchPlaced = _ticks = 0; Job = new(Plan); Phase = StructurePhase.Running;
        Notice = "已确认修补，开始按实际缺口合成；已有内容保留，结束后再次检测。";
        CompanionTaskTracker.Update(_task, CompanionTaskPhase.Ready, result: Notice);
        CompanionMemorySystem.Record("housing_repair_confirmed", _task, null, new
        {
            previewId,
            seed = Housing.Seed,
            materials = Plan.Materials(),
            crafts = budget.Crafts,
            raw = budget.Consumed
        }, _episode);
        Revision++; return Notice;
    }

    private static IStructureSite RepairSite(Player player) => new NonDestructiveStructureSite(new HousingStructureSite(player, Origin!.Value));

    private static void TickHousing(Player player)
    {
        if (++_ticks % 3 != 0) return;
        try
        {
            var site = RepairSite(player);
            if (_repairCrafts.Count > 0)
            {
                var craft = _repairCrafts.Peek();
                // A workbench is placed using the same structure adapter before dependent crafts.
                if (craft.Recipe.Workbench && !EnsureBench(player, site)) { FinishHousing(player, false, Notice); return; }
                if (!RepairCrafting.Make(player, craft, out string error)) { FinishHousing(player, false, error); return; }
                _repairCrafts.Dequeue(); _repairCrafted += craft.Batches * craft.Recipe.Output;
                Notice = error; CompanionMemorySystem.Record("housing_repair_crafted", _task, null, new
                {
                    craft.Recipe.Item,
                    craft.Batches,
                    output = craft.Batches * craft.Recipe.Output,
                    message = error
                }, _episode);
                Revision++; return;
            }
            Job!.Step(site); Notice = $"修补 {Job.Cursor}/{Plan!.Steps.Count} · 新放 {Job.Placed + _repairBenchPlaced} · 跳过 {Job.Failed}";
            CompanionTaskTracker.Update(_task, CompanionTaskPhase.Executing, result: Notice); Revision++;
            if (Job.State != StructureJobState.Running) FinishHousing(player, Job.State == StructureJobState.Completed && Job.Failed == 0, Job.Reason);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { FinishHousing(player, false, "修补已停止：" + ex.GetType().Name); }
    }

    private static bool EnsureBench(Player player, IStructureSite site)
    {
        player.AdjTiles(); if (player.adjTile[TileID.WorkBenches]) return true;
        var bench = Plan!.Steps.FirstOrDefault(s => s.Item == ItemID.WorkBench);
        if (bench == null) { Notice = "合成所需的工作台不在角色附近，请靠近后重新检测。"; return false; }
        if (site.Inspect(bench) == CellState.Empty)
        {
            if (!site.Place(bench, out string error)) { Notice = error; return false; }
            _repairBenchPlaced++;
            CompanionMemorySystem.Record("housing_repair_station", _task, null, new { bench.Dx, bench.Dy, bench.Item }, _episode);
        }
        player.AdjTiles(); if (player.adjTile[TileID.WorkBenches]) return true;
        Notice = "工作台已保留，但角色离它太远，停止合成；请靠近后重新检测。"; return false;
    }

    private static void FinishHousing(Player player, bool built, string reason)
    {
        _repairCrafts.Clear();
        if (!built) Job?.Cancel(reason);
        bool checkedAgain = false;
        try { Housing = HousingInspector.Scan(player, Housing!.Seed); checkedAgain = true; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { reason += "；再次检测未完成：" + ex.Message; }
        bool passed = built && checkedAgain && Housing?.Suitable == true;
        Phase = passed ? StructurePhase.Completed : StructurePhase.Failed;
        Notice = (passed ? "修补完成，再次原生检测通过。" : "修补后仍未通过；" + reason + " " + (checkedAgain ? Housing?.Summary : "再次检测未完成。")) +
            $" 新放 {(Job?.Placed ?? 0) + _repairBenchPlaced} 项，合成 {_repairCrafted} 件；已完成部分保留。";
        CompanionMemorySystem.Record("housing_repair_audit", _task, null, new
        {
            passed,
            checkedAgain,
            suitable = checkedAgain && Housing?.Suitable == true,
            issues = Housing?.Issues,
            placed = (Job?.Placed ?? 0) + _repairBenchPlaced,
            crafted = _repairCrafted,
            failed = Job?.Failed ?? 0,
            reason
        }, _episode);
        if (passed) CompanionTaskTracker.CompleteVerified(_task, Notice);
        else { CompanionTaskTracker.Update(_task, CompanionTaskPhase.Failed, result: Notice); CompanionTaskTracker.Close(_task, "construction_failed", 0); }
        CloseEpisode(passed ? "housing_verified" : "housing_unqualified"); Revision++;
        CompanionUISystem.Message(Notice, passed ? Color.LightGreen : Color.Orange);
    }

    private static string DescribeHousing() => Housing == null ? Notice :
        Housing.Summary + "\n" + string.Join("\n", Housing.Issues.Select(x => "· " + x)) +
        $"\n位置：tile({Housing.Seed.X},{Housing.Seed.Y})，{Housing.Bounds.Width}×{Housing.Bounds.Height}格" +
        "\n修补成品：" + (Plan?.Steps.Count > 0 ? string.Join("、", Plan.Materials().Select(x => $"{Crafting.Name(x.Key)} ×{x.Value}")) : "无自动修补项") +
        "\n合成耗材：" + (RepairBudget?.Consumed.Count > 0 ? string.Join("、", RepairBudget.Consumed.Select(x => $"{Crafting.Name(x.Key)} ×{x.Value}")) : "无需合成") +
        (Missing.Count > 0 ? "\n还缺：" + MissingText(8) : "") + "\n" + Notice;
}

// Stricter than new construction: full footprint reuse, no wires/liquid/actuators,
// no replacement of natural or player tiles and no background-wall replacement.
internal sealed class HousingStructureSite(Player player, Point origin) : IStructureSite
{
    private readonly TerrariaStructureSite _inner = new(player, origin);
    public CellState Inspect(StructureStep s)
    {
        var cells = s.Kind == StepKind.Furniture ? s.Footprint() : new[] { (s.Dx, s.Dy) };
        bool done = true, empty = true;
        foreach (var (x, y) in cells)
        {
            if (!WorldGen.InWorld(origin.X + x, origin.Y + y, 10)) return CellState.Blocked;
            var t = Framing.GetTileSafely(origin.X + x, origin.Y + y);
            if (HousingInspector.Hazard(t)) return CellState.Blocked;
            if (s.Kind == StepKind.Wall)
            { done &= t.WallType == s.Type; empty &= t.WallType == WallID.None; }
            else { done &= t.HasTile && t.TileType == s.Type; empty &= !t.HasTile; }
        }
        return done ? CellState.Done : empty ? CellState.Empty : CellState.Blocked;
    }
    public bool Clear(StructureStep s, out string error) { error = "修补不会拆除现有内容。"; return false; }
    public bool Place(StructureStep s, out string error)
    { if (Inspect(s) != CellState.Empty) { error = "修补位置已变化。"; return false; } return _inner.Place(s, out error); }
}
