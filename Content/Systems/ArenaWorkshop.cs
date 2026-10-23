using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using TerrarAI.Content.Companion;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ObjectData;

namespace TerrarAI.Content.Systems;

public sealed partial class StructureWorkshop
{
    internal static bool ArenaMode { get; private set; }
    internal static ArenaPreparationJob? ArenaJob { get; private set; }
    private static ArenaLayout? _arena;
    private static CraftChainSystem.Proposal? _arenaProposal;
    private static NativeArenaSite? _arenaSite;
    private static string _arenaScene = "", _arenaBlocked = "";
    internal static bool ArenaMoving => ArenaMode && Phase == StructurePhase.Running && _arenaSite?.Chain.Gather.Moving == true;
    internal static bool ArenaAsking => ArenaMode && (Phase == StructurePhase.Preview && CanPrepareArena(Main.LocalPlayer)
        || Phase == StructurePhase.Running && _arenaSite?.Chain.Gather.Asking == true);
    internal static Guid ArenaConfirmationId => _arenaSite?.Chain.Gather.Asking == true ? _arenaSite.Chain.Gather.ApprovalId : PreviewId;
    private static void ResetArena()
    { ArenaMode = false; ArenaJob = null; _arena = null; _arenaProposal = null; _arenaSite = null; _arenaScene = _arenaBlocked = ""; }

    internal static string BeginArena(Player player, bool includePotions = true)
    {
        if (Main.netMode != NetmodeID.SinglePlayer || !player.active || player.dead || CompanionMemorySystem.Session == Guid.Empty)
            return "请在单人世界、角色存活时规划克眼准备。";
        var agent = ChatCoordinator.FindCompanion(); if (agent == null) return "请先唤来同伴。";
        StructureStep Furniture(int id)
        {
            var item = ContentSamples.ItemsByType[id]; var data = TileObjectData.GetTileData(item.createTile, item.placeStyle);
            if (data == null) throw new ArgumentException("缺少家具规格：" + Crafting.Name(id));
            return new(0, 0, StepKind.Furniture, item.createTile, item.placeStyle, id,
                Width: data.Width, Height: data.Height, OriginX: data.Origin.X, OriginY: data.Origin.Y);
        }
        var arena = new ArenaLayout(ItemID.WoodPlatform, TileID.Platforms, ItemID.Wood, TileID.WoodBlock,
            Furniture(ItemID.Campfire), Furniture(ItemID.HeartLantern), includePotions ? new[] {
                new ChainTarget(ItemID.IronskinPotion,2), new ChainTarget(ItemID.RegenerationPotion,2),
                new ChainTarget(ItemID.SwiftnessPotion,2), new ChainTarget(ItemID.LesserHealingPotion,10) } : Array.Empty<ChainTarget>());
        CompanionCommandService.Advance(); CompanionAgent.Cancel(); CompanionConstructionSystem.Hide();
        CompanionDialogueSystem.Cancel(); CompanionInquirySystem.Cancel(); agent.StopCommand("arena_requested");
        ResetHousing(); ResetArena(); ArenaMode = true; _arena = arena;
        _stamp = CompanionCommandService.Capture(); _player = player.whoAmI; _agent = agent.NPC.whoAmI;
        _task = Guid.NewGuid().ToString("N"); _episode = null;
        Plan = arena.Ghost; Job = null; Origin = null; States = null; Missing = new(); Visible = true;
        _ready = false; _previousLeft = false; _ticks = 0;
        CompanionTaskTracker.Begin(_task, "克眼准备：规划→确认→备料施工→核验");
        var feet = player.Bottom.ToTileCoordinates(); PreviewArena(new(feet.X - ArenaLayout.Width / 2, feet.Y - ArenaLayout.Height));
        return DescribeArena();
    }
    internal static void RefreshArena()
    { if (ArenaMode && Phase == StructurePhase.Preview && Origin is { } origin) PreviewArena(origin); }
    private static IEnumerable<StructureStep> ArenaReserved(Point origin)
    {
        foreach (var s in _arena!.Ghost.Steps) yield return s with { Dx = s.Dx + origin.X, Dy = s.Dy + origin.Y };
        for (int row = 0; row < ArenaLayout.Layers; row++) for (int x = 0; x < ArenaLayout.Width; x++)
            for (int h = 1; h <= 3; h++) yield return new(origin.X + x, origin.Y + row * ArenaLayout.Spacing - h, StepKind.Block, 0, 0, 0);
    }
    internal static bool ArenaClearForAudit(Point origin, out string message)
    {
        var planned = _arena!.Ghost.Steps.SelectMany(s => s.Footprint()).Select(p => (p.X + origin.X,p.Y + origin.Y)).ToHashSet();
        foreach (var cell in ArenaReserved(origin).SelectMany(s => s.Footprint()).Distinct())
        {
            if (!WorldGen.InWorld(cell.X, cell.Y, 10)) { message = "竞技场或跳跃净空太靠近世界边界，请换位置。"; return false; }
            if (planned.Contains(cell)) continue;
            var t = Framing.GetTileSafely(cell.X, cell.Y);
            if (t.HasTile || HousingInspector.Hazard(t))
            { message = $"跑动净空 tile({cell.X},{cell.Y}) 有障碍、液体或线路；请换位置或手动处理。"; return false; }
        }
        message = ""; return true;
    }
    private static string ArenaScene(Point origin)
    {
        var text = new StringBuilder();
        foreach (var p in ArenaReserved(origin).SelectMany(s => s.Footprint()).Distinct().OrderBy(p => p.X).ThenBy(p => p.Y))
        {
            var t = Framing.GetTileSafely(p.X,p.Y);
            text.Append(p).Append(':').Append(t.HasTile).Append(',').Append(t.TileType).Append(',').Append(t.WallType)
                .Append(',').Append(t.TileFrameX).Append(',').Append(t.TileFrameY).Append(',').Append(t.Slope).Append(',').Append(t.IsHalfBlock)
                .Append(',').Append(t.LiquidAmount).Append(',').Append(t.LiquidType).Append(',').Append(t.HasActuator).Append(',').Append(t.IsActuated)
                .Append(',').Append(t.RedWire).Append(t.BlueWire).Append(t.GreenWire).Append(t.YellowWire).Append(',').Append(t.TileColor).Append(',').Append(t.WallColor).Append(';');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
    internal static PlatformCell InspectArenaPlatform(ArenaLayout layout, BuildPoint p, IStructureSite fixtures, Point origin)
    {
        bool PlannedReady(int x,int y) => layout.Fixtures.Steps.Any(s =>
            s.Footprint().Contains((x-origin.X,y-origin.Y)) && fixtures.Inspect(s) == CellState.Done);
        if (!WorldGen.InWorld(p.X,p.Y,10) || HousingInspector.Hazard(Framing.GetTileSafely(p.X,p.Y))) return PlatformCell.Protected;
        for (int h=1;h<=3;h++)
        {
            if (!WorldGen.InWorld(p.X,p.Y-h,10)) return PlatformCell.Protected;
            var above = Framing.GetTileSafely(p.X,p.Y-h);
            if (HousingInspector.Hazard(above) || above.HasTile && !PlannedReady(p.X,p.Y-h)) return PlatformCell.Protected;
        }
        return CompanionConstructionSystem.Inspect(p,PlannedReady);
    }
    private static PlatformCell ArenaPlatform(BuildPoint p, ArenaFixtureSite fixtures, Point origin)
        => InspectArenaPlatform(_arena!,p,fixtures,origin);
    private static void PreviewArena(Point origin)
    {
        Origin = origin; Phase = StructurePhase.Preview; PreviewId = Guid.NewGuid();
        ArenaJob = null; _arenaSite = null; _arenaProposal = null; Missing = new(); _arenaBlocked = "";
        try
        {
            var player = Main.player[_player]; var site = new ArenaFixtureSite(player, origin);
            var platform = _arena!.Platforms(new(origin.X,origin.Y), p => ArenaPlatform(p,site,origin));
            States = Plan!.Steps.Select(s => s.Item == ItemID.WoodPlatform ?
                ArenaPlatform(new(origin.X+s.Dx,origin.Y+s.Dy),site,origin) switch {
                    PlatformCell.Platform => CellState.Done, PlatformCell.Empty => CellState.Empty, _ => CellState.Blocked } : site.Inspect(s)).ToArray();
            if (!ArenaClearForAudit(origin,out string blocked)) throw new ArgumentException(blocked);
            if (origin.Y + ArenaLayout.Height >= Main.worldSurface) throw new ArgumentException("请选地表的开阔位置建竞技场。");
            var targets = _arena.Targets(platform, _arena.Fixtures.Steps.Select(site.Inspect).ToArray());
            _arenaProposal = CraftChainSystem.Propose(player,targets,ArenaReserved(origin));
            ArenaJob = new(_arenaProposal.Plan,platform,_arena.Fixtures);
            PreviewId = ArenaJob.PreviewId; Missing = new(_arenaProposal.Plan.Manual); _arenaBlocked = _arenaProposal.Blocked;
            _arenaScene = ArenaScene(origin);
            Notice = Missing.Count > 0 ? "需手动补齐：" + MissingText(4) + "；补齐后刷新计划。" :
                _arenaBlocked.Length > 0 ? _arenaBlocked : "计划就绪：确认后备料、施工；左键场景调整位置。";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { _arenaBlocked = ex.Message; Notice = "不能开工：" + ex.Message + " 左键场景换位置后重算。"; }
        CompanionTaskTracker.Update(_task,CompanionTaskPhase.WaitingForApproval,"确认克眼准备",result:DescribeArena());
        CompanionMemorySystem.Record("arena_preview",_task,null,new { PreviewId, origin, plan=_arenaProposal?.Plan.Signature, _arenaBlocked, missing=Missing });
        Revision++;
    }
    internal static bool CanPrepareArena(Player p) => CompanionCollaborationSystem.CanExecute(p, _task) && !CompanionCollaborationSystem.PlayerOwns(_task) && ArenaMode && Phase == StructurePhase.Preview && ArenaJob != null
        && _arenaProposal?.Plan.CanExecute == true && _arenaBlocked.Length == 0 && Origin != null
        && p.whoAmI == _player && p.active && !p.dead && Main.netMode == NetmodeID.SinglePlayer
        && _stamp.Matches(CompanionMemorySystem.Session,CompanionCommandService.Revision)
        && ChatCoordinator.FindCompanion()?.NPC.whoAmI == _agent;
    private static string ConfirmArena(Player player, Guid id)
    {
        if (!ArenaMode || !Active || player.whoAmI != _player || !player.active || player.dead
            || !_stamp.Matches(CompanionMemorySystem.Session,CompanionCommandService.Revision)) return "准备任务已失效。";
        if (Phase == StructurePhase.Running && _arenaSite?.Chain.Gather.Asking == true)
            return id == _arenaSite.Chain.Gather.ApprovalId ? _arenaSite.Chain.Gather.Approve() : "采集目标已变化，请查看最新提示。";
        if (Phase != StructurePhase.Preview || id == Guid.Empty || id != PreviewId || Origin == null) return "请查看最新竞技场计划后确认。";
        var origin = Origin.Value; var old = _arenaProposal; var oldScene = _arenaScene; var expires = ArenaJob?.Supply.Expires;
        // Recompute every dependency and scene stamp without touching inventory/world.
        PreviewArena(origin);
        ArenaPreviewStamp Stamp(CraftChainSystem.Proposal p,string scene) => new(p.Snapshot,p.Plan.Signature,
            JsonSerializer.Serialize(p.Placements.OrderBy(x=>x.Key)),scene,p.Blocked);
        if (old == null || _arenaProposal == null || expires == null
            || !Stamp(old,oldScene).Matches(Stamp(_arenaProposal,_arenaScene),expires.Value,DateTimeOffset.UtcNow))
            return "现场、库存、制作站、配方或预览时限有变化，已刷新；请查看后再次确认。\n" + DescribeArena();
        if (!CanPrepareArena(player)) return "请先处理缺项，再刷新计划。\n" + DescribeArena();
        if (!ArenaJob!.Confirm(PreviewId,_arenaProposal.Plan.Signature,DateTimeOffset.UtcNow)) return "确认已过期，请重新规划。";
        _arenaSite = new(player,ChatCoordinator.FindCompanion()!.NPC,_arena!,origin,_arenaProposal,_stamp,_task!);
        Phase = StructurePhase.Running; Notice = "已确认：先备料，再铺平台和放设施，最后核验药水与场地。可随时停止。";
        CompanionMemorySystem.Record("arena_confirmed",_task,null,new { id, origin, plan=_arenaProposal.Plan.Signature }); Revision++;
        return Notice;
    }
    private static void TickArena(Player player)
    {
        // Gathering actions expect one update per game frame, as in CraftChainSystem.
        // Only placement is paced to one cell every three updates.
        if (++_ticks % 3 != 0 && ArenaJob?.State != ArenaState.Supplying) return;
        try { ArenaJob!.Step(_arenaSite!); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ArenaJob?.Cancel(_arenaSite,"准备异常："+ex.Message+"；已完成部分保留。"); Phase=StructurePhase.Failed;
            Notice="准备异常："+ex.Message+"；已完成部分保留。";CompanionTaskTracker.SetResult(_task,Notice);
            CompanionTaskTracker.Close(_task,"construction_failed",0);return; }
        var job = ArenaJob!;
        string stage = job.State switch { ArenaState.Supplying=>"备料",ArenaState.Platforms=>"铺设平台",ArenaState.Fixtures=>"放置设施",ArenaState.Completed=>"核验通过",_=>"已停止" };
        Notice = $"{stage} · 备料 {job.Supply.Cursor}/{job.Supply.Plan.Steps.Count} · 平台 {job.Platforms.Cursor}/300 · 设施 {job.Fixtures.Cursor}/{job.Fixtures.Plan.Steps.Count}。{job.Reason}";

        CompanionTaskTracker.Update(_task,ArenaAsking?CompanionTaskPhase.WaitingForApproval:CompanionTaskPhase.Executing,result:Notice);
        Revision++;
        if (job.State is not (ArenaState.Completed or ArenaState.Failed)) return;
        Phase = job.State == ArenaState.Completed ? StructurePhase.Completed : StructurePhase.Failed;
        CompanionMemorySystem.Record("arena_audit",_task,null,new { passed=job.State==ArenaState.Completed,job.Reason,placedPlatforms=job.Platforms.Placed,placedFixtures=job.Fixtures.Placed });
        if (job.State == ArenaState.Completed) CompanionTaskTracker.CompleteVerified(_task,Notice);
        else { CompanionTaskTracker.SetResult(_task,Notice); CompanionTaskTracker.Close(_task,"construction_failed",0); }
        CompanionUISystem.Message(job.Reason,job.State==ArenaState.Completed?Color.LightGreen:Color.Orange);
    }
    internal static string DescribeArena()
    {
        string List(IEnumerable<KeyValuePair<int,int>> items) => string.Join("、",items.Select(x=>$"{Crafting.Name(x.Key)}×{x.Value}"));
        var p = _arenaProposal?.Plan;
        string targets = p == null ? "位置通过检查后计算" : string.Join("、",p.Targets.Select(t=>$"{Crafting.Name(t.Item)}×{t.Count}"));
        var costs = p?.Steps.Where(s=>s.Kind==ChainStepKind.Craft).SelectMany(s=>s.Ingredients!).GroupBy(x=>x.Key).ToDictionary(g=>g.Key,g=>g.Sum(x=>x.Value));
        return "克眼战前准备：3层×100格平台，层距6格；2篝火、2心灯（含木支撑）。\n" +
            (_arena?.Potions.Count > 0 ? "药水：铁皮／再生／敏捷各2，弱效治疗10，交到主背包。\n" : "协作分工：本预览只负责场地，药水由协作计划单独核验。\n") +
            (Origin is {} o ? $"位置 tile({o.X},{o.Y}) 起，100×16格；左键场景可换位置。\n" : "") +
            "需备成品（已扣除原有设施）：" + targets + "\n复用库存：" + (_arenaProposal?.Sources??"待计算") +
            "\n各步合成耗材（含中间件）：" + (costs?.Count>0?List(costs):"无") +
            "\n需采集：" + (p?.Gather.Count>0?List(p.Gather):"无") + "\n需手动补齐：" + (p?.Manual.Count>0?List(p.Manual):"无") +
            "\n补放制作站：" + (_arenaProposal?.Placements.Count>0?string.Join("、",_arenaProposal.Placements.Values.Select(s=>$"{Crafting.Name(s.Item)} tile({s.Dx},{s.Dy})")):"无") +
            (_arenaBlocked.Length>0?"\n条件不足："+_arenaBlocked:"") + "\n" + Notice +
            "\n确认后优先主背包和附近18格内已关闭、未上锁箱子；不足才在现有28格采集范围内收集。保留采集询问偏好，需留在制作站附近。\n" +
            "核验范围：平台与净空、设施完整且开启、药水数量；战斗走位与增益覆盖由玩家实测。";
    }
}

// Strict non-destructive placement: complete furniture frames, style and active
// native buff frames are checked; a disabled lantern/fire is never counted ready.
internal sealed class ArenaFixtureSite(Player player, Point origin) : IStructureSite
{
    private readonly HousingStructureSite _inner = new(player,origin);
    public CellState Inspect(StructureStep step)
    {
        var state = _inner.Inspect(step); if (state != CellState.Done) return state;
        foreach (var (x,y) in step.Footprint())
        {
            var t = Framing.GetTileSafely(origin.X+x,origin.Y+y);
            if (t.IsActuated || t.IsHalfBlock || t.Slope != SlopeType.Solid) return CellState.Blocked;
            if (step.Kind != StepKind.Furniture) continue;
            var d = TileObjectData.GetTileData(step.Type,step.Style);
            if (d == null || TileObjectData.GetTileStyle(t) != step.Style) return CellState.Blocked;
            var frame = FurnitureFrames.Decode(origin.X+x,origin.Y+y,t.TileFrameX,t.TileFrameY,d.Width,d.Height,
                d.CoordinateWidth,d.CoordinatePadding,d.CoordinateHeights,d.CoordinateFullWidth,d.CoordinateFullHeight);
            if (frame == null || frame.X != origin.X+step.Dx-step.OriginX || frame.Y != origin.Y+step.Dy-step.OriginY) return CellState.Blocked;
            if (step.Item == ItemID.Campfire && !ArenaBuffFrames.Campfire(t.TileFrameY) || step.Item == ItemID.HeartLantern && !ArenaBuffFrames.HeartLantern(t.TileFrameY)) return CellState.Blocked;
        }
        return CellState.Done;
    }
    public bool Clear(StructureStep step,out string error) { error="竞技场准备不会拆除现场内容。";return false; }
    public bool Place(StructureStep step,out string error)
    {
        error="现场有变化，未覆盖已有内容。";
        if (Inspect(step)!=CellState.Empty || !_inner.Place(step,out error)) return false;
        if (Inspect(step)==CellState.Done) return true;
        error="已放置设施未通过完整性／开启状态核验，已完成部分保留。"; return false;
    }
}

internal sealed class NativeArenaSite : IArenaSite
{
    internal NativeChainSite Chain { get; }
    public ICraftChainSite Supply => Chain;
    public IPlatformSite Platforms { get; }
    public IStructureSite Fixtures { get; }
    private readonly ArenaLayout _layout; private readonly Point _origin; private readonly Player _player;
    internal NativeArenaSite(Player p,NPC npc,ArenaLayout layout,Point origin,CraftChainSystem.Proposal proposal,ControlStamp stamp,string task)
    {
        _layout=layout;_origin=origin;_player=p;
        Chain=new(p,npc,proposal.Placements,NearbyChests.Ids(p),stamp,task); Fixtures=new ArenaFixtureSite(p,origin);
        Platforms=new TerrariaPlatformSite(p,InspectPlatform);
    }
    private PlatformCell InspectPlatform(BuildPoint p) => StructureWorkshop.InspectArenaPlatform(_layout,p,Fixtures,_origin);
    public bool Audit(out string reason)
    {
        bool platforms=_layout.Platforms(new(_origin.X,_origin.Y),InspectPlatform).Audit(InspectPlatform).Passed;
        bool fixtures=_layout.Fixtures.Steps.All(s=>Fixtures.Inspect(s)==CellState.Done);
        bool potions=_layout.Potions.All(t=>_player.inventory.Take(50).Where(i=>i.type==t.Item).Sum(i=>i.stack)>=t.Count);
        bool clear=StructureWorkshop.ArenaClearForAudit(_origin,out string obstacle);
        bool passed=platforms&&fixtures&&potions&&clear;
        reason=passed?"克眼准备核验通过：300格平台、2篝火、2心灯及支撑完整开启，药水已在主背包。":
            $"核验未通过：平台{(platforms?"通过":"缺失或变形")}，设施{(fixtures?"通过":"缺失、关闭或破损")}，药水{(potions?"齐全":"数量不足")}。{obstacle}已完成部分保留。";
        return passed;
    }
}
