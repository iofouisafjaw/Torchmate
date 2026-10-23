using System;
using System.Linq;
using Microsoft.Xna.Framework;
using TerrarAI.Content.Companion;
using Terraria;
using Terraria.Enums;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrarAI.Content.Systems;

internal enum ConstructionPhase { Idle, Selecting, Preview, Running, Completed, Cancelled, Failed }

public sealed partial class CompanionConstructionSystem : ModSystem
{
    internal static ConstructionPhase Phase { get; private set; }
    internal static PlatformBlueprint? Blueprint { get; private set; }
    internal static BuildRegion? Region { get; private set; }
    internal static PlatformBuildJob? Job { get; private set; }
    internal static Guid PreviewId { get; private set; }
    internal static int Layers { get; private set; } = 3;
    internal static int Spacing { get; private set; } = 6;
    internal static int Revision { get; private set; }
    internal static string Notice { get; private set; } = "";
    internal static bool Visible { get; private set; }
    internal static int? RequestedWidth { get; private set; }
    internal static bool NeedsPause => Phase is ConstructionPhase.Selecting or ConstructionPhase.Preview;
    internal static bool Active => Phase is ConstructionPhase.Selecting or ConstructionPhase.Preview or ConstructionPhase.Running;
    internal static BuildPoint? DragStart { get; private set; }
    private static bool _previousLeft, _selectionReady;
    private static int _player, _agent, _ticks;
    private static ControlStamp _stamp;
    private static string? _task, _episode;
    internal static bool Owns(int npc) => Phase == ConstructionPhase.Running && npc == _agent;
    internal static bool IsTask(string id) => id == _task;
    internal static int Stock(Player player) => player.inventory.Take(50).Where(i => i.type == ItemID.WoodPlatform).Sum(i => i.stack);
    internal static bool CanConfirm(Player player) => Main.netMode == NetmodeID.SinglePlayer && Phase == ConstructionPhase.Preview && Blueprint is { Conflicts: 0 }
        && Stock(player) >= Blueprint.Needed && _stamp.Matches(CompanionMemorySystem.Session, CompanionCommandService.Revision)
        && player.whoAmI == _player && player.active && !player.dead && ChatCoordinator.FindCompanion()?.NPC.whoAmI == _agent;

    internal static string BeginSelection(Player player)
    {
        if (Main.netMode != NetmodeID.SinglePlayer || !player.active || player.dead || CompanionMemorySystem.Session == Guid.Empty)
            return "请在单人世界、角色存活时框选施工区域。";
        var agent = ChatCoordinator.FindCompanion();
        if (agent == null) return "请先到同伴页唤来同伴。";
        CompanionCommandService.Advance(); // Cancels previous construction and invalidates late model responses.
        CompanionAgent.Cancel();
        StructureWorkshop.Hide();
        CompanionDialogueSystem.Cancel(); CompanionInquirySystem.Cancel(); agent.StopCommand("construction_requested");
        _stamp = CompanionCommandService.Capture(); _player = player.whoAmI; _agent = agent.NPC.whoAmI;
        _task = Guid.NewGuid().ToString("N");
        _episode = CompanionMemorySystem.Episodes?.Begin(null, "框选木平台施工区域", new { Layers, Spacing });
        if (_episode != null) CompanionMemorySystem.Episodes?.Link(_episode, null, _task, "木平台施工");
        RequestedWidth = null;
        Blueprint = null; Region = null; Job = null; DragStart = null; PreviewId = Guid.Empty;
        Supply = null; _supplySite = null;
        _previousLeft = false; _selectionReady = false; Visible = true;
        Phase = ConstructionPhase.Selecting; Notice = "拖出范围，或依次单击两个对角；右键或 Esc 取消。";
        CompanionTaskTracker.Begin(_task, "框选木平台施工区域");
        CompanionMemorySystem.Record("construction_selection", _task, null, new { Layers, Spacing }, _episode);
        Revision++; return Notice;
    }

    internal static string BeginRequest(Player player, PlatformRequest request)
    {
        // Validate before superseding an existing task; external/model callers
        // cannot bypass the same limits as the plain-language parser.
        if (request.Layers is < 1 or > 6 || request.Width is < 4 or > 160 || request.Spacing is < 4 or > 12)
            return "蓝图尺寸超出范围：1–6层、每层4–160格、间距4–12格。";
        int previousRevision = Revision;
        string notice = BeginSelection(player);
        if (Revision == previousRevision || Phase != ConstructionPhase.Selecting || _player != player.whoAmI) return notice;
        RequestedWidth = request.Width; Layers = request.Layers; Spacing = request.Spacing;
        CompanionTaskTracker.Begin(_task!, $"{request.Layers}层木平台 · 每层{request.Width}格");
        var anchor = new BuildPoint((int)(player.Center.X / 16) - request.Width / 2, (int)(player.Bottom.Y / 16) - 6);
        Preview(request.At(anchor));
        CompanionMemorySystem.Record("construction_language_request", _task, null,
            new { request.Layers, request.Width, request.Spacing, request.Total, anchor }, _episode);
        return Describe();
    }

    internal static void PlacementInput(BuildPoint at, bool left, bool overUI)
    {
        if (Phase != ConstructionPhase.Preview || RequestedWidth == null) return;
        if (!_selectionReady) { _selectionReady = !left; _previousLeft = left; return; }
        if (left && !_previousLeft && !overUI)
            Preview(new PlatformRequest(Layers, RequestedWidth.Value, Spacing).At(at));
        _previousLeft = left;
    }

    // Called from UpdateUI while the world is paused. A UI click must be released before selection starts.
    internal static void SelectionInput(BuildPoint at, bool left, bool right, bool escape, bool overUI)
    {
        if (Phase != ConstructionPhase.Selecting) return;
        if (right || escape) { Cancel("你取消了框选。"); return; }
        if (!_selectionReady) { _selectionReady = !left; _previousLeft = left; return; }
        if (!overUI && left && !_previousLeft && !DragStart.HasValue) DragStart = at;
        if (!left && _previousLeft && DragStart.HasValue)
        {
            var start = DragStart.Value;
            if (!overUI && start == at)
            { Notice = "起点已选定，单击另一个对角完成框选；右键或 Esc 取消。"; Revision++; }
            else
            {
                DragStart = null;
                if (!overUI) Preview(BuildRegion.Between(start, at));
            }
        }
        _previousLeft = left;
    }

    internal static void Preview(BuildRegion region)
    {
        if (Phase is not (ConstructionPhase.Selecting or ConstructionPhase.Preview)) return;
        Region = region; Blueprint = null; PreviewId = Guid.NewGuid(); Phase = ConstructionPhase.Preview;
        try
        {
            if (region.Left < 10 || region.Top < 10 || region.Right >= Main.maxTilesX - 10 || region.Bottom >= Main.maxTilesY - 10)
                throw new ArgumentException("施工区域太靠近世界边界。");
            Blueprint = new(region, Layers, Spacing, Inspect);
            Notice = Blueprint.Conflicts > 0 ? $"有 {Blueprint.Conflicts} 格与已有地形／建筑冲突，请重新框选。" : "预览不消耗材料；确认后开始施工，可随时取消。";
        }
        catch (ArgumentException ex) { Notice = ex.Message; }
        CompanionTaskTracker.Update(_task, CompanionTaskPhase.WaitingForApproval, "铺设木平台（成品）",
            $"tile({region.Left},{region.Top})–({region.Right},{region.Bottom})", Notice);
        Revision++;
    }
    internal static void SetOptions(int layers, int spacing)
    {
        if (Phase is not (ConstructionPhase.Selecting or ConstructionPhase.Preview)) return;
        Layers = Math.Clamp(layers, 1, 6); Spacing = Math.Clamp(spacing, 4, 12);
        if (Region is { } region)
            Preview(RequestedWidth is int width ? new PlatformRequest(Layers, width, Spacing).At(new(region.Left, region.Bottom)) : region);
        else Revision++;
    }

    internal static string Confirm(Player player, Guid previewId)
    {
        if (previewId == Guid.Empty || previewId != PreviewId || !CanConfirm(player))
            return "蓝图已变化、材料不足或任务已取消，请查看最新预览。";
        var blueprint = Blueprint!;
        if (blueprint.Slots.Any(s => Inspect(s.At) != s.Before))
        { Preview(blueprint.Region); return "现场已变化，已刷新预览，请重新确认。"; }
        Job = new(blueprint); Phase = ConstructionPhase.Running; _ticks = 0;
        Notice = "已确认；每 4 个游戏刻处理一格，游戏暂停时不推进。";
        CompanionTaskTracker.Update(_task, CompanionTaskPhase.Ready, result: Notice);
        CompanionMemorySystem.Record("construction_confirmed", _task, null,
            new { previewId, blueprint.Region, blueprint.Layers, blueprint.Spacing, needed = blueprint.Needed, reused = blueprint.Reused }, _episode);
        Revision++; return Notice;
    }

    internal static void Tick()
    {
        if (!Active) return;
        var player = Main.player[_player];
        if (Main.netMode != NetmodeID.SinglePlayer || !_stamp.Matches(CompanionMemorySystem.Session, CompanionCommandService.Revision)
            || !player.active || player.dead || ChatCoordinator.FindCompanion()?.NPC.whoAmI != _agent)
        { Cancel("角色、同伴或世界会话已变化，施工已取消。"); return; }
        if (Phase != ConstructionPhase.Running || ModContent.GetInstance<CompanionUISystem>().WantsPause || Main.gamePaused) return;
        if (Supply != null) { TickSupply(); return; }
        if (++_ticks % 4 != 0) return;
        var job = Job!; int placed = job.Placed;
        var site = new TerrariaPlatformSite(player); job.Step(site);
        if (job.Placed != placed)
        {
            var p = job.Blueprint.Slots[job.Cursor - 1].At;
            CompanionMemorySystem.Record("construction_placement", _task, null,
                new { p.X, p.Y, item = ItemID.WoodPlatform, consumed = 1, placed = job.Placed }, _episode);
        }
        Notice = $"进度 {job.Cursor}/{job.Blueprint.Slots.Count} · 新放 {job.Placed} · 复用 {job.Reused}";
        CompanionTaskTracker.Update(_task, CompanionTaskPhase.Executing, result: Notice);
        Revision++;
        if (job.State == PlatformJobState.Running) return;
        Phase = job.State == PlatformJobState.Completed ? ConstructionPhase.Completed : ConstructionPhase.Failed;
        Notice = job.Reason + $" 新放 {job.Placed} 格，消耗 {job.Placed} 个木平台，复用 {job.Reused} 格。";
        var audit = job.Audit ?? job.Blueprint.Audit(Inspect);
        CompanionMemorySystem.Record("construction_audit", _task, null,
            new { audit.Passed, audit.Required, audit.Present, audit.Missing, audit.Blocked, placed = job.Placed, reused = job.Reused, reason = job.Reason }, _episode);
        if (job.State == PlatformJobState.Completed) CompanionTaskTracker.CompleteVerified(_task, Notice);
        else { CompanionTaskTracker.Update(_task, CompanionTaskPhase.Failed, result: Notice); CompanionTaskTracker.Close(_task, "construction_failed", 0); }
        CloseEpisode(job.State == PlatformJobState.Completed ? "construction_verified" : "construction_failed");
        CompanionUISystem.Message("平台工坊：" + Notice, job.State == PlatformJobState.Completed ? Color.LightGreen : Color.Orange);
    }

    internal static void Cancel(string reason)
    {
        if (!Active) return;
        if (Supply != null && _supplySite != null) Supply.Cancel(_supplySite, reason);
        Job?.Cancel(reason); Phase = ConstructionPhase.Cancelled; PreviewId = Guid.Empty; DragStart = null;
        Notice = reason + $" 已放 {Job?.Placed ?? 0} 格，已消耗材料不退回；保留已完成部分。";
        if (Supply != null) Notice += $" 已合成 {Supply.Crafted} 个平台，消耗普通木材 {Supply.WoodConsumed}。";
        CompanionTaskTracker.Update(_task, CompanionTaskPhase.Stopped, result: Notice);
        CompanionTaskTracker.Close(_task, "construction_cancelled", 0);
        CompanionMemorySystem.Record("construction_cancelled", _task, null,
            new { reason, placed = Job?.Placed ?? 0, crafted = Supply?.Crafted ?? 0, woodConsumed = Supply?.WoodConsumed ?? 0 }, _episode);
        CloseEpisode("construction_cancelled"); Revision++;
    }
    private static void CloseEpisode(string reason)
    { if (_episode != null) CompanionMemorySystem.Episodes?.Close(_episode, reason); _episode = null; }
    internal static void Hide() { if (Active) Cancel("你取消了施工。"); Visible = false; Revision++; }
    internal static string Describe()
    {
        string area = Region is { } r ? $"范围：{r.Width}×{r.Height} 格 · tile({r.Left},{r.Top})–({r.Right},{r.Bottom})" : "尚未选择范围";
        string materials = Blueprint is { } b ? $"需要 {b.Needed} 个成品木平台 · 复用 {b.Reused} 格 · 冲突 {b.Conflicts} 格" : "拖动框选后计算材料";
        string audit = Job?.Audit is { } a ? $"\n核验：{a.Present}/{a.Required} 格符合，缺失／不合格 {a.Missing} 格。" : "";
        return $"{area}\n{Layers} 层 · 垂直间距 {Spacing} 格（从底部向上铺设）\n{materials}\n{SupplyDescription()}\n{Notice}{audit}\n验收范围：各层平台连续、数量与间距符合蓝图；不代表 Boss 场地通用合格。";
    }
    internal static PlatformCell Inspect(BuildPoint p)
        => Inspect(p, null);
    internal static PlatformCell Inspect(BuildPoint p, Func<int,int,bool>? allowedNeighbor)
    {
        if (!WorldGen.InWorld(p.X, p.Y, 5)) return PlatformCell.Protected;
        var tile = Framing.GetTileSafely(p.X, p.Y);
        if (tile.HasTile && TileID.Sets.Platforms[tile.TileType] && !tile.IsActuated && !tile.IsHalfBlock && tile.Slope == SlopeType.Solid)
            return PlatformCell.Platform;
        if (tile.HasTile || tile.WallType != WallID.None || tile.LiquidAmount != 0 || tile.HasActuator || tile.IsActuated || tile.RedWire || tile.BlueWire || tile.GreenWire || tile.YellowWire)
            return PlatformCell.Protected;
        // Leave one tile of clearance around existing foreground objects, including tree trunks and furniture.
        for (int x = p.X - 1; x <= p.X + 1; x++)
            for (int y = p.Y - 1; y <= p.Y + 1; y++)
            {
                var neighbor = Framing.GetTileSafely(x, y);
                if (neighbor.HasTile && !TileID.Sets.Platforms[neighbor.TileType] && allowedNeighbor?.Invoke(x,y)!=true) return PlatformCell.Protected;
            }
        return PlatformCell.Empty;
    }
    public override void PostUpdateWorld() { if (Main.netMode == NetmodeID.SinglePlayer) Tick(); }
    private static void Reset()
    {
        Cancel("离开世界，施工已取消。"); Phase = ConstructionPhase.Idle; Visible = false;
        RequestedWidth = null;
        Blueprint = null; Region = null; Job = null; PreviewId = Guid.Empty; DragStart = null;
        Supply = null; _supplySite = null;
        _task = _episode = null; Layers = 3; Spacing = 6; Notice = ""; Revision++;
    }
    public override void OnWorldLoad() => Reset();
    public override void OnWorldUnload() => Reset();
    public override void Unload() => Reset();
}

// Placement and inventory mutation share the game thread; failed placement restores the reserved item.
internal sealed class TerrariaPlatformSite(Player player, Func<BuildPoint,PlatformCell>? inspect = null) : IPlatformSite
{
    public PlatformCell Inspect(BuildPoint point) => inspect?.Invoke(point) ?? CompanionConstructionSystem.Inspect(point);
    public int Available => CompanionConstructionSystem.Stock(player);
    public bool TryPlace(BuildPoint p, out string error)
    {
        error = "";
        if (Inspect(p) != PlatformCell.Empty) { error = "现场出现变化，没有覆盖已有方块。"; return false; }
        var material = player.inventory.Take(50).FirstOrDefault(i => i.type == ItemID.WoodPlatform && i.stack > 0);
        if (material == null) { error = "背包成品木平台不足。"; return false; }
        material.stack--;
        bool placed = false;
        try
        {
            WorldGen.PlaceTile(p.X, p.Y, TileID.Platforms, mute: true, forced: false, plr: player.whoAmI, style: 0);
            var tile = Framing.GetTileSafely(p.X, p.Y);
            placed = tile.HasTile;
            if (!placed) error = $"tile({p.X},{p.Y}) 放置失败，材料已退回。";
            else if (tile.TileType != TileID.Platforms || Inspect(p) != PlatformCell.Platform)
                error = "生成的方块形态未达标，已停止；该格材料已消耗。";
            return placed;
        }
        catch (Exception ex)
        {
            var tile = Framing.GetTileSafely(p.X, p.Y);
            placed = tile.HasTile;
            error = "放置异常：" + ex.GetType().Name;
            return placed;
        }
        finally
        {
            if (!placed) material.stack++;
            else if (material.stack == 0) material.TurnToAir();
        }
    }
}
