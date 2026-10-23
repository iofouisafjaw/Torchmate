using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerrarAI.Content.Companion;

namespace TerrarAI.Content.Systems;

// A failure is queued, never resumed inside a module's Close callback. The next world update
// reads fresh stock and checks visit/control/owner identity before requesting the model.
public sealed class CompanionRecoverySystem : ModSystem
{
    internal sealed record Owner(TaskRecoverySession Flow,AgentLoop Loop,Player Player,NPC Companion,CheatPermit Cheat,string CheatGoal);
    private sealed record TaskContext(Owner Owner,string Kind);
    private sealed record PendingFailure(CompanionTaskView View,string Code,DateTimeOffset At);
    private static TaskContext? _current;
    private static PendingFailure? _pending;
    private static Owner? _dispatch;
    private static (Player Player,string Goal)? _input;
    internal static bool PreservingLineage => _dispatch != null && _current?.Owner == _dispatch;
    private sealed class Scope(Action close):IDisposable {public void Dispose()=>close();}

    internal static IDisposable Input(Player player,string text)
    {
        var previous=_input;_input=(player,text);return new Scope(()=>_input=previous);
    }
    internal static IDisposable Dispatch(Player player,AgentLoop loop,AgentCall call,Owner? continuing,CheatPermit cheat,string cheatGoal)
    {
        var previous=_dispatch;
        if(call.Tool is "build" or "platform" or "craft_chain" or "garden" or "prepare_eye" or "expedition" or "combat" or "chop" or "mine" or "replan")
        {
            var companion=ChatCoordinator.FindCompanion()?.NPC;
            _dispatch=continuing ?? (companion==null||CompanionMemorySystem.Session==Guid.Empty?null:
                new Owner(new(CompanionMemorySystem.Session,player.whoAmI,companion.whoAmI,loop.Goal),loop,player,companion,cheat,cheatGoal));
        }
        return new Scope(()=>_dispatch=previous);
    }
    internal static void Invalidate()
    {
        // Native Begin intentionally invalidates the previous task. Only an explicit synchronous
        // dispatch may carry its original lineage into a new preview; external commands cannot.
        if(_current!=null&&_dispatch!=_current.Owner)_current.Owner.Flow.Cancel();
        _current=null;_pending=null;
    }
    internal static void Began(CompanionTaskView view)
    {
        string kind=Kind(view.TaskId);
        if(kind.Length==0){Invalidate();return;}
        if(_current?.Owner.Flow.TaskId==view.TaskId)return; // e.g. platform title refresh
        var player=_input?.Player??Main.LocalPlayer;
        var companion=ChatCoordinator.FindCompanion()?.NPC;
        if(Main.netMode!=NetmodeID.SinglePlayer||!player.active||player.dead||companion==null||CompanionMemorySystem.Session==Guid.Empty)return;
        var owner=_dispatch;
        if(owner==null)
        {
            Invalidate();string goal=_input?.Goal??view.Goal;
            owner=new(new(CompanionMemorySystem.Session,player.whoAmI,companion.whoAmI,goal),new AgentLoop(goal),player,companion,
                CheatTools.Authority.Capture(),goal);
        }
        string previousTask = owner.Flow.TaskId;
        if(!ReferenceEquals(owner.Player,player)||!ReferenceEquals(owner.Companion,companion)||
            !owner.Flow.Bind(view.TaskId,CompanionCommandService.Capture()))return;
        CompanionCollaborationSystem.RecoveryBound(previousTask, view.TaskId);
        _current=new(owner,kind);_pending=null;
        Log("recovery_task_bound",new{owner.Flow.RootId,view.TaskId,kind,owner.Flow.Goal,owner.Flow.Stamp});
    }
    internal static void Closed(CompanionTaskView view,string code="completed")
    {
        if(_current?.Owner.Flow.TaskId!=view.TaskId)return;
        if(view.Phase==CompanionTaskPhase.Failed){_pending=new(view,code,DateTimeOffset.UtcNow);return;}
        Invalidate(); // completed, player cancellation, rejection and world changes never retry
    }
    private static string Kind(string id)=>CraftChainSystem.IsTask(id)?"craft_chain":CompanionConstructionSystem.IsTask(id)?"platform":
        StructureWorkshop.IsTask(id)?StructureWorkshop.ArenaMode?"prepare_eye":StructureWorkshop.HousingMode?"housing":"build":
        CompanionGardenSystem.IsTask(id)?"garden":CompanionExpeditionSystem.IsTask(id)?"expedition":CompanionCombatSystem.IsTask(id)?"combat":
        ChatCoordinator.FindCompanion()?.RecoveryKind(id)??"";
    internal static bool OwnsFailure(string id)=>_current?.Owner.Flow.TaskId==id&&(_pending!=null||_current.Owner.Flow.Recovering);
    private static bool OwnerMatches(Owner owner)=>Main.netMode==NetmodeID.SinglePlayer&&!Main.gameMenu&&
        _current?.Owner==owner&&owner.Player.whoAmI>=0&&owner.Player.whoAmI<Main.maxPlayers&&
        ReferenceEquals(Main.player[owner.Player.whoAmI],owner.Player)&&owner.Player.active&&!owner.Player.dead&&!owner.Player.ghost&&
        owner.Companion.active&&ReferenceEquals(ChatCoordinator.FindCompanion()?.NPC,owner.Companion)&&
        owner.Flow.Matches(CompanionMemorySystem.Session,CompanionCommandService.Revision,owner.Player.whoAmI,owner.Companion.whoAmI,true);
    internal static bool Allows(Owner owner)=>OwnerMatches(owner)&&owner.Flow.Allows(CompanionMemorySystem.Session,CompanionCommandService.Revision,
        owner.Player.whoAmI,owner.Companion.whoAmI,true,DateTimeOffset.UtcNow);
    internal static bool Request(Owner owner)=>Allows(owner)&&owner.Flow.Request(CompanionMemorySystem.Session,CompanionCommandService.Revision,
        owner.Player.whoAmI,owner.Companion.whoAmI,true,DateTimeOffset.UtcNow);
    internal static bool TryStop(string text)
    {
        if((_pending==null&&_current?.Owner.Flow.Recovering!=true)||!RecoveryPolicy.IsStop(text))return false;
        Invalidate();CompanionAgent.Cancel();return true;
    }
    private static TaskFailureReceipt Receipt(TaskContext context,CompanionTaskView view,string code)
    {
        var player=context.Owner.Player;
        var bag=NearbyChests.Stock(player,false);var total=NearbyChests.Stock(player);
        var stock=total.Keys.Concat(bag.Keys).Distinct().OrderBy(i=>i).Select(i=>new RecoveryStock(i,Crafting.Name(i),
            bag.GetValueOrDefault(i),Math.Max(0,total.GetValueOrDefault(i)-bag.GetValueOrDefault(i)))).ToArray();
        string completed=context.Kind switch {
            "craft_chain"=>CraftChainSystem.RecoveryProgress,
            "platform"=>CompanionConstructionSystem.RecoveryProgress,
            "build" or "housing" or "prepare_eye"=>StructureWorkshop.RecoveryProgress,
            "garden"=>CompanionGardenSystem.RecoveryProgress,
            "expedition"=>CompanionExpeditionSystem.Describe(),
            "combat"=>CompanionCombatSystem.Describe(), _=>"没有可独立核实的完成收据"
        };
        if(context.Kind=="gather")completed=ChatCoordinator.FindCompanion()?.RecoveryProgress??completed;
        bool replan=context.Kind is "craft_chain" or "platform" or "build" or "housing" or "prepare_eye" ||
            context.Kind=="garden"&&CompanionGardenSystem.Job is {} garden&&garden.Cursor<garden.Plan.Targets.Count;
        if(view.Result.Contains("异常"))replan=false; // unknown side effects require a human inspection
        return new(view.TaskId,context.Kind,code,view.Result,completed,stock,
            CompanionAgent.Snapshot(player),replan);
    }
    internal static string Replan(Owner owner)
    {
        if(!Allows(owner)||_current is not {} current||Kind(owner.Flow.TaskId)!=current.Kind||
            !owner.Flow.Replan(CompanionMemorySystem.Session,CompanionCommandService.Revision,owner.Player.whoAmI,owner.Companion.whoAmI,true,DateTimeOffset.UtcNow))
            return "原任务已失效、不能自动重规划或已达恢复上限；请明确说明新任务。";
        Log("recovery_replan_requested",new{owner.Flow.RootId,owner.Flow.TaskId,owner.Flow.Replans});
        // Original module owns the target, blueprint and coordinates. No model arguments enter here.
        try
        {
            return current.Kind switch {
                "craft_chain"=>CraftChainSystem.Replan(owner.Player),
                "platform"=>CompanionConstructionSystem.Replan(owner.Player),
                "build" or "housing" or "prepare_eye"=>StructureWorkshop.Replan(owner.Player),
                "garden"=>CompanionGardenSystem.Replan(owner.Player),
                _=>"此类任务需要玩家重新明确下令。"
            };
        }
        catch(Exception ex)when(ex is not OutOfMemoryException)
        {return "重规划未完成，未自动执行；请核对原现场和已完成部分："+ex.Message;}
    }
    internal static string CurrentResources(Owner owner)
    {
        var bag=NearbyChests.Stock(owner.Player,false);var total=NearbyChests.Stock(owner.Player);
        var types=total.Keys.Concat(bag.Keys).Distinct().OrderBy(i=>i).ToArray();
        return CompanionAgent.Snapshot(owner.Player)+"\n恢复实时库存（覆盖旧收据中的库存，主背包前50格／可用附近箱子）："+
            (types.Length==0?"空":string.Join("、",types.Take(40).Select(i=>
                $"{Crafting.Name(i)}（背包{bag.GetValueOrDefault(i)}／箱子{Math.Max(0,total.GetValueOrDefault(i)-bag.GetValueOrDefault(i))}）")))+
            (types.Length>40?$"；另有{types.Length-40}种，可用inventory/chests补查。":"");
    }
    public override void PostUpdateWorld()
    {
        if(_pending is not {} pending||_current is not {} context)return;
        var view=CompanionTaskTracker.Current??pending.View;
        if(view.TaskId!=pending.View.TaskId){Invalidate();return;}
        if(!OwnerMatches(context.Owner)||CompanionTaskTracker.Current?.TaskId!=view.TaskId||Kind(view.TaskId)!=context.Kind)
        {Invalidate();return;}
        if(DateTimeOffset.UtcNow-pending.At>TimeSpan.FromMinutes(5))
        {Invalidate();CompanionUISystem.Message("恢复等待已超时，未继续请求模型；已完成部分保留，请重新下令。",Color.Orange);return;}
        if(Main.gamePaused||ModContent.GetInstance<CompanionUISystem>().WantsPause)return;
        _pending=null;
        try
        {
            var receipt=Receipt(context,view,pending.Code);
            if(!OwnerMatches(context.Owner))return;
            CompanionUISystem.Message(receipt.Describe(),Color.Orange);
            bool resume=context.Owner.Flow.Fail(receipt,CompanionMemorySystem.Session,CompanionCommandService.Revision,
                context.Owner.Player.whoAmI,context.Owner.Companion.whoAmI,true,DateTimeOffset.UtcNow);
            Log("long_task_failure",new{context.Owner.Flow.RootId,receipt,resume,context.Owner.Flow.Failures});
            if(!resume){CompanionUISystem.Message("自动恢复次数已用尽，已停止；保留已完成部分，请查看收据后重新下令。",Color.Orange);Invalidate();return;}
            CompanionAgent.Recover(context.Owner,receipt);
        }
        catch(Exception ex)when(ex is not OutOfMemoryException)
        {Invalidate();CompanionUISystem.Message("恢复收据读取未完成，已停止自动恢复；已完成部分保留："+ex.Message,Color.Orange);}
    }
    private static void Log(string kind,object data)=>CompanionMemorySystem.Record(kind,null,null,data);
    public override void OnWorldLoad()=>Invalidate();
    public override void OnWorldUnload()=>Invalidate();
    public override void Unload(){Invalidate();_dispatch=null;_input=null;}
}
