using TerrarAI.Content.Companion;
using TerrarAI.Content.Systems;
using Terraria;
using Terraria.ModLoader;

int passed=0;
void Check(bool yes,string name){if(!yes)throw new Exception("FAIL: "+name);passed++;Console.WriteLine("PASS: "+name);}
var system=new CompanionRecoverySystem();var tracker=new CompanionTaskTracker();
void Reset()
{
    system.Unload();tracker.OnWorldLoad();Modules.Ids.Clear();Modules.Previews=Modules.Executions=0;Modules.ThrowReplan=false;
    Main.netMode=0;Main.gameMenu=Main.gamePaused=false;
    Main.player=new[]{new Player{whoAmI=0},new Player{whoAmI=1}};ChatCoordinator.Agent=new();
    CompanionMemorySystem.Session=Guid.NewGuid();CompanionMemorySystem.Events.Clear();
    CompanionCommandService.Revision=0;CompanionAgent.Resumes=0;CompanionAgent.LastOwner=null;CompanionAgent.LastReceipt=null;
    CompanionUISystem.Messages.Clear();ModContent.GetInstance<CompanionUISystem>().WantsPause=false;
    NearbyChests.Bag=new(){{9,8}};NearbyChests.Boxes=new(){{9,12},{1,3}};NearbyChests.Throw=false;
    StructureWorkshop.ArenaMode=StructureWorkshop.HousingMode=false;CompanionGardenSystem.Job=null;
}
string Begin(string kind="craft_chain",string goal="original exact player goal")
{using var input=CompanionRecoverySystem.Input(Main.LocalPlayer,goal);Modules.Begin(kind);return Modules.Ids[kind];}
void Fail(string id,string reason="station blocked")
{CompanionTaskTracker.SetResult(id,reason);CompanionTaskTracker.Close(id,"construction_failed",0);}

Reset();var id=Begin();Fail(id);
Check(CompanionAgent.Resumes==0,"native Close queues recovery rather than calling model reentrantly");
NearbyChests.Bag[9]=4;NearbyChests.Boxes[9]=19;system.PostUpdateWorld();
var owner=CompanionAgent.LastOwner!;
Check(CompanionAgent.Resumes==1&&owner.Flow.Goal=="original exact player goal","direct native task retains human goal through task closure");
Check(CompanionAgent.LastReceipt!.Stock.Single(s=>s.Type==9) is {Backpack:4,Chests:19},"receipt uses stock after failure and before recovery delivery");
Check(CompanionAgent.LastReceipt.Code=="construction_failed"&&CompanionAgent.LastReceipt.Completed.Contains("two craft"),"machine failure code and confirmed progress survive the actual task tracker");
Check(CompanionTaskTracker.Current is {Closed:true,GoalVerified:false,Phase:CompanionTaskPhase.Failed},"starting recovery does not mark the original task completed");
system.PostUpdateWorld();CompanionTaskTracker.Close(id,"construction_failed",0);system.PostUpdateWorld();
Check(CompanionAgent.Resumes==1,"duplicate world pumps and repeated Close cannot duplicate recovery");
NearbyChests.Bag[9]=30;
Check(CompanionRecoverySystem.CurrentResources(owner).Contains("背包30")&&NearbyChests.Boxes[9]==19,"each next prompt reads current resources without taking anything");
Guid oldPreview=Modules.PreviewId;
using(CompanionRecoverySystem.Dispatch(Main.LocalPlayer,owner.Loop,new("replan"),owner,owner.Cheat,owner.CheatGoal))
    Check(CompanionRecoverySystem.Replan(owner).Contains("confirmation"),"replan routes to source module preview");
var newId=Modules.Ids["craft_chain"];
Check(newId!=id&&Modules.PreviewId!=oldPreview&&Modules.Executions==0,"intentional handoff gets new task/confirmation, never executes");
Check(owner.Flow.TaskId==newId&&!owner.Flow.Cancelled&&!owner.Flow.Recovering&&owner.Flow.Replans==1,
    "native Advance and Agent.Cancel preserve only explicitly dispatched lineage");
Fail(id);system.PostUpdateWorld();Check(CompanionAgent.Resumes==1,"late old task failure cannot resume over the new preview");
Fail(newId);system.PostUpdateWorld();Check(CompanionAgent.Resumes==2&&ReferenceEquals(owner,CompanionAgent.LastOwner),"second actual failure reuses original goal and recovery budget");
using(CompanionRecoverySystem.Dispatch(Main.LocalPlayer,owner.Loop,new("replan"),owner,owner.Cheat,owner.CheatGoal))CompanionRecoverySystem.Replan(owner);
Fail(Modules.Ids["craft_chain"]);system.PostUpdateWorld();
Check(CompanionAgent.Resumes==2&&owner.Flow.Cancelled&&CompanionUISystem.Messages.Any(s=>s.Contains("次数已用尽")),"third failed child stops automatically and shows final receipt");

foreach(string terminal in new[]{"cancel","reject","done","verified"})
{
    Reset();id=Begin();
    if(terminal=="done")CompanionTaskTracker.Finish(id,"finished",2);
    else if(terminal=="verified")CompanionTaskTracker.CompleteVerified(id,"verified");
    else CompanionTaskTracker.Close(id,terminal=="reject"?"player_rejected":"construction_cancelled",0);
    system.PostUpdateWorld();Check(CompanionAgent.Resumes==0,"normal terminal outcome never auto-recovers: "+terminal);
}
foreach(string change in new[]{"control","world","dead","ghost","player identity","npc identity","npc absent","multiplayer","new tracker","agent cancelled"})
{
    Reset();id=Begin();Fail(id);
    switch(change)
    {
        case "control":CompanionCommandService.Advance();break;case "world":CompanionMemorySystem.Session=Guid.NewGuid();break;
        case "dead":Main.LocalPlayer.dead=true;break;case "ghost":Main.LocalPlayer.ghost=true;break;
        case "player identity":Main.player[0]=new(){whoAmI=0};break;case "npc identity":ChatCoordinator.Agent=new();break;
        case "npc absent":ChatCoordinator.Agent=null;break;case "multiplayer":Main.netMode=1;break;
        case "new tracker":CompanionTaskTracker.Begin("unrelated","new user goal");break;case "agent cancelled":CompanionAgent.Cancel();break;
    }
    system.PostUpdateWorld();Check(CompanionAgent.Resumes==0,"queued failure cannot survive invalid scope: "+change);
}
foreach(string pause in new[]{"game","preview"})
{
    Reset();id=Begin();Fail(id);
    if(pause=="game")Main.gamePaused=true;else ModContent.GetInstance<CompanionUISystem>().WantsPause=true;
    system.PostUpdateWorld();Check(CompanionAgent.Resumes==0,"queued model work waits during "+pause+" pause");
    Main.gamePaused=false;ModContent.GetInstance<CompanionUISystem>().WantsPause=false;
    system.PostUpdateWorld();Check(CompanionAgent.Resumes==1,"unpaused recovery rechecks and resumes: "+pause);
}
Reset();id=Begin();Fail(id);Check(CompanionRecoverySystem.TryStop("算了"),"plain cancellation can withdraw a queued recovery");
system.PostUpdateWorld();Check(CompanionAgent.Resumes==0,"cancelled queue cannot later call the model");
Reset();id=Begin();Fail(id);NearbyChests.Throw=true;system.PostUpdateWorld();
Check(CompanionAgent.Resumes==0&&CompanionUISystem.Messages.Any(s=>s.Contains("读取未完成")),"resource adapter exception reports honest failure without resuming");

foreach(string kind in new[]{"craft_chain","platform","build","expedition","combat"})
{
    Reset();id=Begin(kind);Fail(id);system.PostUpdateWorld();
    Check(CompanionAgent.LastReceipt?.Kind==kind,"known long-task source maps to the correct receipt: "+kind);
    owner=CompanionAgent.LastOwner!;
    if(kind is "expedition" or "combat")
    {
        int before=Modules.Previews;
        using(CompanionRecoverySystem.Dispatch(Main.LocalPlayer,owner.Loop,new("replan"),owner,owner.Cheat,owner.CheatGoal))CompanionRecoverySystem.Replan(owner);
        Check(Modules.Previews==before&&!CompanionAgent.LastReceipt!.CanReplan,"destructive/moving task does not restart without a new player command: "+kind);
    }
}
foreach(string mode in new[]{"arena","housing"})
{
    Reset();StructureWorkshop.ArenaMode=mode=="arena";StructureWorkshop.HousingMode=mode=="housing";
    id=Begin("build");Fail(id);system.PostUpdateWorld();
    Check(CompanionAgent.LastReceipt?.Kind==(mode=="arena"?"prepare_eye":"housing"),"workshop subtype remains explicit in receipt: "+mode);
}
Reset();id=Begin();Fail(id,"执行异常，现场可能有未核验变化");system.PostUpdateWorld();
Check(CompanionAgent.LastReceipt?.CanReplan==false,"unknown exception-side-effects permit queries and questions, not blind replan");
Reset();id=Begin();Fail(id);system.PostUpdateWorld();owner=CompanionAgent.LastOwner!;
Modules.ThrowReplan=true;
using(CompanionRecoverySystem.Dispatch(Main.LocalPlayer,owner.Loop,new("replan"),owner,owner.Cheat,owner.CheatGoal))
    Check(CompanionRecoverySystem.Replan(owner).Contains("未完成"),"module replan exception cannot pretend a preview was accepted");
Check(Modules.Previews==1&&Modules.Executions==0,"failed replan never consumes resources or creates a phantom task");

// Agent-origin handoff: the real bridge must carry goal/history across End/Advance/Cancel.
Reset();var originalLoop=new AgentLoop("model-origin exact goal",new[]{new AgentStep("查询","source fact","look")});
using(CompanionRecoverySystem.Dispatch(Main.LocalPlayer,originalLoop,new("craft_chain","熔炉",1),null,default,"original cheat goal"))
{CompanionAgent.Cancel();Modules.Begin("craft_chain");}
id=Modules.Ids["craft_chain"];Fail(id);system.PostUpdateWorld();
Check(ReferenceEquals(CompanionAgent.LastOwner!.Loop,originalLoop)&&originalLoop.Goal=="model-origin exact goal"&&originalLoop.Steps[0].Result=="source fact",
    "model handoff restores the actual original AgentLoop and history, not a new guessed goal");
Check(CompanionAgent.LastOwner!.Cheat==default&&CompanionAgent.LastOwner.CheatGoal=="original cheat goal","recovery cannot acquire a newer cheat permit");
foreach(string reason in new[]{"no_target","no_more_targets","missing_tool","planner_error","step_limit"})
{
    Reset();id=Begin("gather");
    CompanionTaskTracker.Close(id,reason,1);
    CompanionTaskTracker.SetResult(id,"latest partial-gather report");
    Check(CompanionRecoverySystem.OwnsFailure(id),"legacy gather wait defers to failure callback: "+reason);
    system.PostUpdateWorld();
    Check(CompanionTaskTracker.Current is {Phase:CompanionTaskPhase.Failed,GoalVerified:false}&&
        CompanionAgent.LastReceipt is {Kind:"gather",CanReplan:false,Reason:"latest partial-gather report"}&&
        CompanionAgent.LastReceipt.Completed.Contains("not bag pickups"),"partial gathering reports fresh facts without reauthorizing collection: "+reason);
}
Check(Modules.Executions==0,"all adapter scenarios ran without any fake native mutation, model or real game world");
Console.WriteLine($"Passed {passed} recovery adapter checks.");
