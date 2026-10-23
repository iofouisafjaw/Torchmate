using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerrarAI.Content.Companion;

namespace TerrarAI.Content.Systems;

// Cooperative projects own no action queue or inventory code. Each launch delegates to an
// existing workshop, which still owns its preview, consent ticket, execution and audit.
public sealed class CompanionCollaborationSystem : ModSystem
{
    internal static CollaborationPlan? Current { get; private set; }
    private static Player? _player;
    private static NPC? _npc;
    private static Guid _session;
    private static bool _transition;
    private static int _boundRevision, _logged;
    private static Point? _arenaOrigin, _houseOrigin;
    private static StructurePlan? _house;
    internal static bool Preserving => _transition;
    private static bool Matches(Player p) => Current != null && ReferenceEquals(p, _player) && p.active && !p.dead && !p.ghost
        && p.whoAmI >= 0 && p.whoAmI < Main.maxPlayers && ReferenceEquals(Main.player[p.whoAmI], p)
        && Main.netMode == NetmodeID.SinglePlayer && !Main.gameMenu && _session == CompanionMemorySystem.Session
        && ReferenceEquals(ChatCoordinator.FindCompanion()?.NPC, _npc) && _npc?.active == true;
    internal static bool PlayerOwns(string? task) => Current?.Next is { Owner: CollaborationOwner.Player } s && s.TaskId == task;
    internal static bool CanExecute(Player player, string? task) => Current?.Next is not {} work || work.TaskId != task ||
        Matches(player) && !Current.Paused && work.Owner == CollaborationOwner.Companion;

    internal static bool TryInput(Player player, string text, out string response)
    {
        response = "";
        var command = CollaborationRequest.Parse(text, Current != null);
        if (command == null) return false;
        if (command.Operation == "clarify")
        {
            if (Current is { Complete: false } && Matches(player))
            { CaptureSite(); Halt(); Current.Pause("新要求需要澄清，未继续原执行"); Flush(); }
            response = "协作目前支持克眼准备或扩建1–3间NPC住房；请分别说明目标，再说「药水我来」「备料你来」协商分工。额外限制尚未落实，未启动施工；已有协作会暂停。"; return true;
        }
        if (command.Operation == "start")
        {
            if (Main.netMode != NetmodeID.SinglePlayer || !player.active || player.dead || player.ghost || CompanionMemorySystem.Session == Guid.Empty)
            { response = "请在单人世界、角色存活时开始协作。"; return true; }
            var npc = ChatCoordinator.FindCompanion()?.NPC;
            if (npc == null) { response = "请先唤来同伴。"; return true; }
            Halt();
            Current = new(command.Target, command.Rooms); _player = player; _npc = npc;
            _session = CompanionMemorySystem.Session; _arenaOrigin = _houseOrigin = null; _logged = 0;
            _house = command.Target == "housing" ? StructureLibrary.List().Single(e => e.Id == $"preset:npc{command.Rooms}").Load() : null;
            response = Current.Describe() + "\n可以说「药水我来」「竞技场你来」或「备料我来」「住房你来」；说「继续协作」打开下一项预览。";
            Flush(); return true;
        }
        if (!Matches(player)) { response = "协作的角色、同伴或世界已变化，请重新发起协作。"; Reset(); return true; }
        try
        {
            switch (command.Operation)
            {
                case "status": response = Current!.Describe(); break;
                case "pause": CaptureSite(); Halt(); Current!.Pause("玩家暂停；已完成部分保留"); response = Current.Describe(); break;
                case "assign":
                    string id = command.Target switch { "竞技场" => "arena", "药水" => "potions", "住房备料" or "备料" => "materials", _ => "housing" };
                    // Validate before interrupting useful work.
                    if (!Current!.Steps.Any(s => s.Id == id && s.State != CollaborationState.Verified))
                        throw new ArgumentException("当前计划没有这项未完成工作。");
                    CaptureSite(); Halt(); Current.Assign(id, command.Owner); response = Current.Describe(); break;
                case "resize":
                    if (_houseOrigin != null) throw new ArgumentException("原项目已经选址，不能扩大施工范围；请另开协作扩建任务选择新位置。");
                    Current!.Resize(command.Rooms); Halt();
                    _house = StructureLibrary.List().Single(e => e.Id == $"preset:npc{command.Rooms}").Load();
                    response = Current.Describe(); break;
                case "continue": response = Continue(player); break;
                case "audit": response = Audit(player); break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { response = "协作未继续：" + ex.Message; }
        Flush(); return true;
    }
    private static ChainTarget[] PotionTargets => new[] { new ChainTarget(ItemID.IronskinPotion, 2), new ChainTarget(ItemID.RegenerationPotion, 2),
        new ChainTarget(ItemID.SwiftnessPotion, 2), new ChainTarget(ItemID.LesserHealingPotion, 10) };
    private static ChainTarget[] Targets(CollaborationStep step)
    {
        if (step.Id == "potions") return PotionTargets;
        var steps = _house!.Steps.AsEnumerable();
        if (_houseOrigin is {} origin && _player != null)
        {
            var site = new HousingStructureSite(_player, origin);
            steps = steps.Where(s => site.Inspect(s) != CellState.Done);
        }
        return steps.GroupBy(s => s.Item).Select(g => new ChainTarget(g.Key, g.Count())).ToArray();
    }

    private static string Continue(Player player)
    {
        var plan = Current!;
        if (plan.Complete) return FinalAudit(player);
        plan.Resume(); var step = plan.Next!;
        if (step.TaskId.Length > 0 && CompanionTaskTracker.Current is { Closed: false } active && active.TaskId == step.TaskId)
            return plan.Describe() + "\n" + active.Result;
        if (step.State == CollaborationState.Failed && step.Evidence.Contains("异常"))
            return "上次执行存在未知副作用，请先手动核对现场，再明确发起新任务；没有自动重试。\n" + plan.Describe();
        // Reuse already verified world effects; there is nothing to approve when no action is needed.
        if (step.Owner == CollaborationOwner.Companion &&
            (step.Kind == "supplies" && Targets(step).Length == 0 || step.Kind == "housing" && _houseOrigin != null) &&
            Inspect(player, step, out string reused))
        {
            string audit = "audit:" + Guid.NewGuid().ToString("N");
            plan.Bind(step.Id, audit, plan.Revision); plan.Observe(audit, plan.Revision, CollaborationState.Verified, reused);
            return plan.Complete ? FinalAudit(player) : plan.Describe() + "\n已复用实际完成部分；说「继续协作」查看下一项。";
        }
        if (step.Id == "housing" && _houseOrigin != null)
        {
            var bag = NearbyChests.Stock(player, false);
            var material = plan.Steps.Single(s => s.Id == "materials");
            if (Targets(material).Any(t => bag.GetValueOrDefault(t.Item) < t.Count))
            {
                plan.RecheckFailed("materials", "原现场剩余施工材料不足；保留已完成部分，重新计算缺口");
                return Continue(player);
            }
        }
        if (step.Owner == CollaborationOwner.Player && step.Kind == "supplies")
            return plan.Describe() + "\n请把以下成品放入主背包，再说「核验协作」：" +
                string.Join("、", Targets(step).Select(t => $"{Crafting.Name(t.Item)}×{t.Count}"));
        string result;
        _transition = true;
        try
        {
            CompanionDialogueSystem.Cancel();
            result = step.Kind switch
            {
                "arena" => StructureWorkshop.BeginArena(player, false),
                "housing" => StructureWorkshop.Begin(player, StructureLibrary.List().Single(e => e.Id == $"preset:npc{plan.Rooms}")),
                _ => CraftChainSystem.BeginTargets(player, Targets(step), "协作：" + step.Name)
            };
            if (CompanionTaskTracker.Current is { Closed: false } task &&
                (step.Kind == "supplies" ? CraftChainSystem.IsTask(task.TaskId) : StructureWorkshop.IsTask(task.TaskId)))
            {
                _boundRevision = plan.Revision; plan.Bind(step.Id, task.TaskId, _boundRevision);
            }
            if (step.Id == "arena" && _arenaOrigin is {} arena) StructureWorkshop.CollaborationPreview(arena);
            if (step.Id == "housing" && _houseOrigin is {} house) StructureWorkshop.Preview(house);
            if (step.Id == "arena" && step.Owner == CollaborationOwner.Player && _arenaOrigin == null) StructureWorkshop.Replace();
            CaptureSite();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Halt();
            if (plan.Next?.TaskId is { Length: > 0 } failed) plan.Observe(failed, plan.Revision, CollaborationState.Failed, "预览异常：" + ex.Message);
            throw;
        }
        finally { _transition = false; }
        if (step.Owner == CollaborationOwner.Player)
            result += "\n这项由你施工；同伴的施工确认已禁用。请选定预览位置，完成后说「核验协作」。";
        if (step.Kind is "arena" or "housing") CompanionUISystem.RevealStructureSelection();
        return plan.Describe() + "\n" + result;
    }
    private static void CaptureSite()
    {
        var next = Current?.Next;
        if (next == null || !StructureWorkshop.IsTask(next.TaskId) || StructureWorkshop.Origin == null) return;
        if (next.Id == "arena") _arenaOrigin = StructureWorkshop.Origin;
        if (next.Id == "housing") _houseOrigin = StructureWorkshop.Origin;
    }
    private static bool Inspect(Player player, CollaborationStep step, out string evidence)
    {
        try { return InspectUnchecked(player, step, out evidence); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { evidence = "核验异常，需要人工检查：" + ex.Message; return false; }
    }
    private static bool InspectUnchecked(Player player, CollaborationStep step, out string evidence)
    {
        CaptureSite();
        if (step.Kind == "supplies")
        {
            var bag = NearbyChests.Stock(player, false);
            var missing = Targets(step).Where(t => bag.GetValueOrDefault(t.Item) < t.Count).ToArray();
            evidence = missing.Length == 0 ? "主背包成品数量核验通过" : "还缺：" + string.Join("、", missing.Select(t => $"{Crafting.Name(t.Item)}×{t.Count - bag.GetValueOrDefault(t.Item)}"));
            return missing.Length == 0;
        }
        return StructureWorkshop.CollaborationAudit(player, step.Id == "arena", step.Id == "arena" ? _arenaOrigin : _houseOrigin, _house, Current!.Rooms, out evidence);
    }
    private static string Audit(Player player)
    {
        var plan = Current!; if (plan.Complete) return FinalAudit(player);
        var step = plan.Next!;
        if (step.Owner != CollaborationOwner.Player) return "这项由同伴执行，进度只接受执行模块的实际验收。\n" + plan.Describe();
        if (plan.Paused) return "协作已暂停，请先说「继续协作」。";
        if (!Inspect(player, step, out string evidence)) return "核验未通过：" + evidence;
        // Player supply work has no native child. Bind an audit receipt, never a consent ticket.
        string task = step.TaskId.Length == 0 ? "player:" + Guid.NewGuid().ToString("N") : step.TaskId;
        if (step.TaskId.Length == 0) plan.Bind(step.Id, task, plan.Revision);
        Halt(); plan.Observe(task, plan.Revision, CollaborationState.Verified, evidence);
        return plan.Complete ? FinalAudit(player) : plan.Describe() + "\n说「继续协作」查看下一项预览。";
    }
    private static string FinalAudit(Player player)
    {
        var plan = Current!;
        foreach (var step in plan.Steps.Where(s => s.Id != "materials"))
            if (!Inspect(player, step, out string evidence))
            { plan.RecheckFailed(step.Id, evidence); plan.Pause("最终复查未通过：" + evidence); return "协作最终复查未通过，已停止：" + evidence + "；请核对现场后继续协作生成新预览。"; }
        return "协作最终复查通过。\n" + plan.Describe();
    }
    internal static void Observe(CompanionTaskView? task)
    {
        if (_transition || Current?.Next is not {} step || task == null || task.TaskId != step.TaskId) return;
        if (_player == null || !Matches(_player)) { Current.Pause("角色、同伴或世界已变化"); Flush(); return; }
        CaptureSite();
        if (task.Closed && !task.GoalVerified && task.Phase != CompanionTaskPhase.Failed)
        { Current.Pause("执行任务已取消；旧许可失效"); Flush(); return; }
        if (step.Owner == CollaborationOwner.Player) return;
        string evidence = task.Result;
        var state = task.GoalVerified ? CollaborationState.Verified : task.Phase == CompanionTaskPhase.Failed ? CollaborationState.Failed :
            task.Phase == CompanionTaskPhase.Executing ? CollaborationState.Running : CollaborationState.Preview;
        if (task.GoalVerified && _player != null && !Inspect(_player, step, out evidence)) state = CollaborationState.Failed;
        if (Current.Observe(task.TaskId, _boundRevision, state, evidence))
        {
            Flush();
            if (state is CollaborationState.Verified or CollaborationState.Failed)
            {
                CompanionUISystem.Message(Current.Describe() + "\n" + (Current.Complete ? FinalAudit(_player!) : "可查询协作进度；继续前需查看新预览。"), Color.LightBlue);
                Flush();
            }
        }
    }
    // Called only after the existing recovery owner successfully binds its original lineage.
    internal static void RecoveryBound(string previous, string task)
    {
        if (previous.Length == 0 || Current?.Next is not {} step || step.TaskId != previous || Current.Paused) return;
        _boundRevision = Current.Revision; Current.Bind(step.Id, task, _boundRevision); Flush();
    }
    internal static void ExternalCommand()
    {
        if (_transition || CompanionRecoverySystem.PreservingLineage || Current == null || Current.Complete || Current.Paused) return;
        CaptureSite(); Current.Pause("收到其他指令；旧确认失效"); Flush();
    }
    private static void Halt()
    {
        bool previous = _transition; _transition = true;
        try { CompanionCommandService.Advance(); CompanionAgent.Cancel(); CompanionDialogueSystem.Cancel();
            CompanionInquirySystem.Cancel(); ChatCoordinator.FindCompanion()?.StopCommand("player_stopped"); }
        finally { _transition = previous; }
    }
    private static void Flush()
    {
        if (Current == null) return;
        foreach (var entry in Current.Journal.TakeLast(Current.EventCount - _logged)) CompanionMemorySystem.Record("collaboration_event", Current.Id, null, new { entry, Current.Rooms, Current.Scenario });
        _logged = Current.EventCount;
    }
    private static void Reset() { Current = null; _player = null; _npc = null; _house = null; _arenaOrigin = _houseOrigin = null; _logged = 0; }
    public override void PostUpdateWorld()
    {
        if (Current == null) return;
        if (_player == null || !Matches(_player)) { Halt(); Reset(); }
    }
    public override void OnWorldLoad() => Reset();
    public override void OnWorldUnload() => Reset();
    public override void Unload() => Reset();
}
