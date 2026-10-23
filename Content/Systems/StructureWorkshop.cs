using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using TerrarAI.Content.Companion;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ObjectData;

namespace TerrarAI.Content.Systems;

internal enum StructurePhase { Idle, Placing, Preview, Running, Completed, Cancelled, Failed }

// 建筑工坊: pick a blueprint, place a ghost, check materials and obstacles, then the companion builds it
// cell by cell from the player's inventory. Nothing is placed or removed before the player confirms.
public sealed partial class StructureWorkshop : ModSystem
{
    internal static StructurePhase Phase { get; private set; }
    internal static StructurePlan? Plan { get; private set; }
    internal static Point? Origin { get; private set; }
    internal static CellState[]? States { get; private set; }
    internal static Dictionary<int, int> Missing { get; private set; } = new();
    internal static StructureBuildJob? Job { get; private set; }
    internal static Guid PreviewId { get; private set; }
    internal static string Notice { get; private set; } = "";
    internal static int Revision { get; private set; }
    internal static bool Visible { get; private set; }
    internal static bool NeedsPause => Phase == StructurePhase.Placing || Phase == StructurePhase.Preview && !CompanionCollaborationSystem.PlayerOwns(_task);
    internal static bool Active => Phase is StructurePhase.Placing or StructurePhase.Preview or StructurePhase.Running;
    private static bool _previousLeft, _ready;
    private static int _player, _agent, _ticks;
    private static ControlStamp _stamp;
    private static string? _task, _episode;
    internal static bool Owns(int npc) => Phase == StructurePhase.Running && npc == _agent;
    internal static bool IsTask(string id) => id == _task;

    internal static int Have(Player player, int item) =>
        player.inventory.Take(58).Where(i => i != null && i.type == item).Sum(i => i.stack);

    internal static string Begin(Player player, StructureLibrary.Entry entry)
    {
        if (Main.netMode != NetmodeID.SinglePlayer || !player.active || player.dead || CompanionMemorySystem.Session == Guid.Empty)
            return "请在单人世界、角色存活时使用建筑工坊。";
        var agent = ChatCoordinator.FindCompanion();
        if (agent == null) return "请先到同伴页唤来同伴。";
        StructurePlan plan;
        try { plan = entry.Load(); }
        catch (Exception ex) { return $"读取蓝图「{entry.Name}」失败：{ex.Message}"; }
        if (plan.Steps.Count == 0) return $"蓝图「{entry.Name}」里没有能建造的东西。";
        CompanionCommandService.Advance();   // cancels other construction and stale model results
        CompanionAgent.Cancel();
        ResetHousing(); ResetArena();
        CompanionConstructionSystem.Hide();
        CompanionDialogueSystem.Cancel(); CompanionInquirySystem.Cancel(); agent.StopCommand("construction_requested");
        _stamp = CompanionCommandService.Capture(); _player = player.whoAmI; _agent = agent.NPC.whoAmI;
        _task = Guid.NewGuid().ToString("N");
        _episode = CompanionMemorySystem.Episodes?.Begin(null, "建造：" + plan.Name, new { plan.Name, plan.Source, plan.Width, plan.Height });
        if (_episode != null) CompanionMemorySystem.Episodes?.Link(_episode, null, _task, "建造：" + plan.Name);
        Plan = plan; Origin = null; States = null; Missing = new(); Job = null; PreviewId = Guid.Empty;
        _previousLeft = false; _ready = false; Visible = true;
        Phase = StructurePhase.Placing;
        Notice = "移动鼠标选位置（鼠标处是左下角），左键放下预览；右键或 Esc 取消。";
        CompanionTaskTracker.Begin(_task, "建造：" + plan.Name);
        CompanionMemorySystem.Record("structure_selected", _task, null,
            new { plan.Name, plan.Source, plan.Width, plan.Height, steps = plan.Steps.Count, skipped = plan.Skipped }, _episode);
        Revision++; return $"选好了「{plan.Name}」（{plan.Width}×{plan.Height}）。" + Notice;
    }

    internal static Point GhostOrigin(Point mouseTile) => new(mouseTile.X, mouseTile.Y - (Plan?.Height ?? 1) + 1);

    // From UpdateUI while paused. The click that opened the workshop must be released first.
    internal static void PlacingInput(Point mouseTile, bool left, bool right, bool escape, bool overUI)
    {
        if (Phase is not (StructurePhase.Placing or StructurePhase.Preview)) return;
        if (right || escape) { Cancel("你取消了建造。"); return; }
        if (Phase != StructurePhase.Placing && !ArenaMode) return;
        if (!_ready) { _ready = !left; _previousLeft = left; return; }
        if (!overUI && left && !_previousLeft)
        { if (ArenaMode) PreviewArena(GhostOrigin(mouseTile)); else if (HousingMode) PreviewHousing(mouseTile); else Preview(GhostOrigin(mouseTile)); }
        _previousLeft = left;
    }

    internal static void Replace()
    {
        if (Phase != StructurePhase.Preview) return;
        Phase = StructurePhase.Placing; Origin = null; States = null; PreviewId = Guid.Empty; _ready = false;
        Notice = "重新选位置：左键放下，右键或 Esc 取消。"; Revision++;
    }

    internal static void Preview(Point origin)
    {
        var plan = Plan!;
        Origin = origin; PreviewId = Guid.NewGuid(); Phase = StructurePhase.Preview;
        if (origin.X < 10 || origin.Y < 10 || origin.X + plan.Width >= Main.maxTilesX - 10 || origin.Y + plan.Height >= Main.maxTilesY - 10)
        { States = null; Notice = "太靠近世界边界了，换个位置。"; Revision++; return; }
        var player = Main.player[_player];
        var site = BuildSite(player, origin);
        States = plan.Steps.Select(site.Inspect).ToArray();
        Missing = plan.Missing(States, item => Have(player, item));
        int build = States.Count(s => s is CellState.Empty or CellState.Clearable);
        int clear = States.Count(s => s == CellState.Clearable), blocked = States.Count(s => s == CellState.Blocked);
        Notice = Missing.Count > 0 ? "材料不够：" + MissingText(6) + "。"
            : blocked > 0 ? $"有 {blocked} 格被你的东西挡住，会跳过不拆；可以换个位置。"
            : clear > 0 ? $"会先挖掉 {clear} 格泥土／石块／草，掉落物留在地上。"
            : "材料够了，位置也空着。";
        CompanionTaskTracker.Update(_task, CompanionTaskPhase.WaitingForApproval, "建造 " + plan.Name,
            $"tile({origin.X},{origin.Y}) 起 {plan.Width}×{plan.Height}", Notice);
        Revision++;
    }

    internal static string MissingText(int max) => string.Join("、",
        Missing.OrderByDescending(p => p.Value).Take(max).Select(p => $"{Lang.GetItemNameValue(p.Key)} ×{p.Value}"))
        + (Missing.Count > max ? " 等" : "");

    internal static bool CanConfirm(Player player) => CompanionCollaborationSystem.CanExecute(player, _task) && !CompanionCollaborationSystem.PlayerOwns(_task) && Main.netMode == NetmodeID.SinglePlayer && Phase == StructurePhase.Preview
        && States != null && Missing.Count == 0 && States.Any(s => s != CellState.Done)
        && _stamp.Matches(CompanionMemorySystem.Session, CompanionCommandService.Revision)
        && player.whoAmI == _player && player.active && !player.dead && ChatCoordinator.FindCompanion()?.NPC.whoAmI == _agent;

    internal static string Confirm(Player player, Guid previewId)
    {
        if (CompanionCollaborationSystem.PlayerOwns(_task)) return "这项由玩家负责；请完成后说「核验协作」，或重新协商交给同伴。";
        if (!CompanionCollaborationSystem.CanExecute(player, _task)) return "协作权限或角色已变化，请重新发起任务。";
        if (ArenaMode) return ConfirmArena(player, previewId);
        if (HousingMode) return ConfirmHousing(player, previewId);
        if (previewId == Guid.Empty || previewId != PreviewId || !CanConfirm(player))
            return "预览已变化、材料不够或任务已取消，请看最新预览。";
        var site = BuildSite(player, Origin!.Value);
        if (Plan!.Steps.Select(site.Inspect).Where((s, i) => s != States![i]).Any())
        { Preview(Origin.Value); return "现场有变化，已刷新预览，请再确认一次。"; }
        Job = new(Plan); Phase = StructurePhase.Running; _ticks = 0;
        Notice = "开工了。打开同伴面板会暂停施工。";
        CompanionTaskTracker.Update(_task, CompanionTaskPhase.Ready, result: Notice);
        CompanionMemorySystem.Record("structure_confirmed", _task, null,
            new { previewId, Plan.Name, origin = new[] { Origin.Value.X, Origin.Value.Y },
                materials = Plan.Materials().Select(p => $"{Lang.GetItemNameValue(p.Key)} ×{p.Value}").ToArray() }, _episode);
        Revision++; return Notice;
    }

    internal static void Tick()
    {
        if (!Active) return;
        var player = Main.player[_player];
        if (Phase == StructurePhase.Running && !CompanionCollaborationSystem.CanExecute(player, _task))
        { Cancel("协作权限或角色已变化，施工停止。"); return; }
        if (Main.netMode != NetmodeID.SinglePlayer || !_stamp.Matches(CompanionMemorySystem.Session, CompanionCommandService.Revision)
            || !player.active || player.dead || ChatCoordinator.FindCompanion()?.NPC.whoAmI != _agent)
        { Cancel("角色、同伴或世界已变化，施工已取消。"); return; }
        if (Phase != StructurePhase.Running || ModContent.GetInstance<CompanionUISystem>().WantsPause || Main.gamePaused) return;
        if (ArenaMode) { TickArena(player); return; }
        if (HousingMode) { TickHousing(player); return; }
        if (++_ticks % 3 != 0) return;
        var job = Job!;
        try { job.Step(BuildSite(player, Origin!.Value)); }
        catch(Exception ex)when(ex is not OutOfMemoryException)
        { job.Cancel("建造异常："+ex.Message);Phase=StructurePhase.Failed;Notice="建造异常，已完成部分保留："+ex.Message;
            CompanionTaskTracker.SetResult(_task,Notice);CompanionTaskTracker.Close(_task,"construction_failed",0);return; }
        Notice = $"进度 {job.Cursor}/{job.Plan.Steps.Count} · 新放 {job.Placed} · 复用 {job.Reused}"
            + (job.Cleared > 0 ? $" · 挖掉 {job.Cleared}" : "") + (job.Failed > 0 ? $" · 跳过 {job.Failed}" : "");
        if (job.Cursor % 10 == 0 || job.State != StructureJobState.Running)
            CompanionTaskTracker.Update(_task, CompanionTaskPhase.Executing, result: Notice);
        Revision++;
        if (job.State == StructureJobState.Running) return;
        bool verified; string collaborativeAudit = "";
        try { verified=job.Verified(BuildSite(player,Origin!.Value));
            if (verified) verified = CollaborationFinalAudit(player, out collaborativeAudit); }
        catch(Exception ex)when(ex is not OutOfMemoryException)
        { Phase=StructurePhase.Failed;Notice="蓝图核验异常，已完成部分保留："+ex.Message;
            CompanionTaskTracker.SetResult(_task,Notice);CompanionTaskTracker.Close(_task,"construction_failed",0);
            CloseEpisode("structure_failed");return; }
        Phase = verified ? StructurePhase.Completed : StructurePhase.Failed;
        Notice = (verified?job.Reason:"蓝图未完全达标，保留已完成部分。"+collaborativeAudit+(job.State==StructureJobState.Failed?job.Reason:""))+" "+Notice;
        CompanionMemorySystem.Record("structure_finished", _task, null,
            new { verified, state = job.State.ToString(), job.Placed, job.Reused, job.Cleared, job.Failed, problems = job.Problems, job.Reason }, _episode);
        if (verified) CompanionTaskTracker.CompleteVerified(_task, Notice);
        else { CompanionTaskTracker.Update(_task, CompanionTaskPhase.Failed, result: Notice); CompanionTaskTracker.Close(_task, "construction_failed", 0); }
        CloseEpisode(verified ? "structure_built" : "structure_failed");
        CompanionUISystem.Message("建筑工坊：" + Notice, verified ? Color.LightGreen : Color.Orange);
    }

    internal static void Cancel(string reason)
    {
        if (!Active) return;
        if (ArenaMode) ArenaJob?.Cancel(_arenaSite, reason);
        Job?.Cancel(reason); Phase = StructurePhase.Cancelled; PreviewId = Guid.Empty;
        Notice = reason + (Job != null ? $" 已放 {Job.Placed} 格，已用的材料不退回，建好的部分保留。" : "");
        if (ArenaMode && ArenaJob is { } arena)
            Notice = reason + $" 已放平台 {arena.Platforms.Placed} 格、设施 {arena.Fixtures.Placed} 项；已采集、合成及建好的部分保留。";
        if (HousingMode)
        {
            _repairCrafts.Clear();
            Notice = reason + $" 已放 {(Job?.Placed ?? 0) + _repairBenchPlaced} 项，合成 {_repairCrafted} 件；已完成部分保留。";
        }
        CompanionTaskTracker.Update(_task, CompanionTaskPhase.Stopped, result: Notice);
        CompanionTaskTracker.Close(_task, "construction_cancelled", 0);
        CompanionMemorySystem.Record("structure_cancelled", _task, null, new { reason, placed = Job?.Placed ?? 0 }, _episode);
        CloseEpisode("structure_cancelled"); Revision++;
    }

    internal static void Hide() { if (Active) Cancel("你取消了建造。"); Visible = false; Revision++; }

    internal static string Describe()
    {
        if (ArenaMode) return DescribeArena();
        if (HousingMode) return DescribeHousing();
        if (Plan == null) return "还没有选蓝图。";
        var p = Plan;
        string where = Origin is { } o ? $"位置：tile({o.X},{o.Y}) 起，{p.Width}×{p.Height} 格" : "还没放下位置";
        var mats = p.Materials().OrderByDescending(m => m.Value).Take(8)
            .Select(m => $"{Lang.GetItemNameValue(m.Key)} ×{m.Value}");
        string skipped = p.Skipped.Count > 0 ? "\n不会建造：" + string.Join("；", p.Skipped) : "";
        string problems = Job?.Problems.Count > 0 ? "\n没放成：" + string.Join("；", Job.Problems) : "";
        return $"蓝图：{p.Name}（{p.Source}）\n{where}\n材料：{string.Join("、", mats)}{skipped}\n{Notice}{problems}";
    }

    private static void CloseEpisode(string reason)
    { if (_episode != null) CompanionMemorySystem.Episodes?.Close(_episode, reason); _episode = null; }

    public override void PostUpdateWorld() { if (Main.netMode == NetmodeID.SinglePlayer) Tick(); }

    private static void Reset()
    {
        Cancel("离开世界，施工已取消。"); Phase = StructurePhase.Idle; Visible = false;
        Plan = null; Origin = null; States = null; Missing = new(); Job = null; PreviewId = Guid.Empty;
        ResetHousing(); ResetArena();
        _task = _episode = null; Notice = ""; Revision++;
    }
    public override void OnWorldLoad() => Reset();
    public override void OnWorldUnload() => Reset();
    public override void Unload() { Reset(); StructureLibrary.Unload(); }
}

// World access for one build. Runs on the game thread; an item is consumed only when a tile/wall appears.
internal sealed class TerrariaStructureSite(Player player, Point origin) : IStructureSite
{
    private static readonly HashSet<int> Natural = new()
    {
        TileID.Dirt, TileID.Stone, TileID.Mud, TileID.ClayBlock, TileID.Sand, TileID.Silt, TileID.Slush, TileID.SnowBlock,
        TileID.IceBlock, TileID.Ash, TileID.Ebonstone, TileID.Crimstone, TileID.Pearlstone, TileID.Ebonsand, TileID.Crimsand,
        TileID.Pearlsand, TileID.Sandstone, TileID.HardenedSand, TileID.CorruptIce, TileID.FleshIce, TileID.HallowedIce,
        TileID.Marble, TileID.Granite
    };

    private static bool IsNatural(int type) => Natural.Contains(type) || TileID.Sets.Grass[type] || Main.tileCut[type];

    private Point At(int dx, int dy) => new(origin.X + dx, origin.Y + dy);

    public CellState Inspect(StructureStep step)
    {
        var p = At(step.Dx, step.Dy);
        if (!WorldGen.InWorld(p.X, p.Y, 5)) return CellState.Blocked;
        var tile = Framing.GetTileSafely(p.X, p.Y);
        switch (step.Kind)
        {
            case StepKind.Wall:
                if (tile.WallType == step.Type) return CellState.Done;
                return tile.WallType == WallID.None ? CellState.Empty : CellState.Blocked;  // keep existing walls
            case StepKind.Block:
                if (tile.HasTile && (tile.TileType == step.Type || TileID.Sets.Grass[step.Type] && tile.TileType == TileID.Dirt))
                    return CellState.Done;
                if (!tile.HasTile) return CellState.Empty;
                return IsNatural(tile.TileType) ? CellState.Clearable : CellState.Blocked;
            default:
                if (tile.HasTile && tile.TileType == step.Type) return CellState.Done;
                bool clear = false;
                foreach (var (fx, fy) in step.Footprint())
                {
                    var c = At(fx, fy);
                    var t = Framing.GetTileSafely(c.X, c.Y);
                    if (!t.HasTile) continue;
                    if (!IsNatural(t.TileType)) return CellState.Blocked;
                    clear = true;
                }
                return clear ? CellState.Clearable : CellState.Empty;
        }
    }

    public bool Clear(StructureStep step, out string error)
    {
        error = "";
        var cells = step.Kind == StepKind.Furniture ? step.Footprint() : new[] { (step.Dx, step.Dy) };
        foreach (var (dx, dy) in cells)
        {
            var p = At(dx, dy);
            var tile = Framing.GetTileSafely(p.X, p.Y);
            if (!tile.HasTile) continue;
            if (!IsNatural(tile.TileType)) { error = "被你的东西挡住，没拆"; return false; }
            WorldGen.KillTile(p.X, p.Y);
            if (Framing.GetTileSafely(p.X, p.Y).HasTile) { error = "挖不动（可能需要更好的镐）"; return false; }
        }
        return true;
    }

    public bool Place(StructureStep step, out string error)
    {
        error = "";
        var material = player.inventory.Take(58).FirstOrDefault(i => i != null && i.type == step.Item && i.stack > 0);
        if (material == null) { error = "材料不足：" + Lang.GetItemNameValue(step.Item); return false; }
        var p = At(step.Dx, step.Dy);
        bool placed = false;
        material.stack--;
        try
        {
            if (step.Kind == StepKind.Wall)
            {
                WorldGen.PlaceWall(p.X, p.Y, material.createWall, mute: true);
                var tile = Framing.GetTileSafely(p.X, p.Y);
                placed = tile.WallType == material.createWall;
                if (placed) { tile.WallColor = step.Paint; WorldGen.SquareWallFrame(p.X, p.Y); }
                else error = "墙放不上";
                return placed;
            }
            int type = material.createTile, style = material.placeStyle;
            var data = step.Kind == StepKind.Furniture ? TileObjectData.GetTileData(type, style) : null;
            if (data != null && (data.Width > 1 || data.Height > 1))
            {
                if (TileObject.CanPlace(p.X, p.Y, type, style, step.Direction, out var obj) && TileObject.Place(obj))
                {
                    TileObjectData.CallPostPlacementPlayerHook(p.X, p.Y, type, style, step.Direction, obj.alternate, obj);
                    WorldGen.SquareTileFrame(p.X, p.Y);
                }
                else WorldGen.PlaceTile(p.X, p.Y, type, mute: true, forced: false, plr: player.whoAmI, style: style);
            }
            else WorldGen.PlaceTile(p.X, p.Y, type, mute: true, forced: false, plr: player.whoAmI, style: style);
            var placedTile = Framing.GetTileSafely(p.X, p.Y);
            placed = placedTile.HasTile && placedTile.TileType == type;
            if (!placed) { error = step.Kind == StepKind.Furniture ? "家具放不下（空间或支撑不够）" : "方块放不上（旁边没有支撑）"; return false; }
            if (step.Kind == StepKind.Block && step.Shape != 0 && WorldGen.CanPoundTile(p.X, p.Y))
            {
                if (step.Shape == 1) { WorldGen.SlopeTile(p.X, p.Y, 0); WorldGen.PoundTile(p.X, p.Y); }
                else WorldGen.SlopeTile(p.X, p.Y, step.Shape - 1);
            }
            placedTile.TileColor = step.Paint;
            return true;
        }
        catch (Exception ex)
        {
            var tile = Framing.GetTileSafely(p.X, p.Y);
            placed = step.Kind == StepKind.Wall ? tile.WallType == material.createWall : tile.HasTile && tile.TileType == material.createTile;
            error = "放置出错：" + ex.GetType().Name;
            return placed;
        }
        finally
        {
            if (!placed) material.stack++;
            else if (material.stack == 0) material.TurnToAir();
        }
    }
}
