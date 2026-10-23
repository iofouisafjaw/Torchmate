using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using TerrarAI.Content.Companion;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrarAI.Content.Systems;

public sealed class CompanionGardenSystem:ModSystem
{
    internal static GardenJob? Job{get;private set;}
    private static NativeGardener? _site;
    private static Player? _player;
    private static NPC? _agent;
    private static ControlStamp _stamp;
    private static string? _task;
    private static string _stock="";
    private static string _seedSources="";
    private static HashSet<(BuildPoint At,GardenCrop Crop)>? _recoveryTargets;
    internal static bool Active=>Job?.State is GardenState.Preview or GardenState.Running;
    internal static bool Moving=>_site?.Moving==true;
    internal static bool Asking=>Active&&(_site?.Asking==true||Job?.State==GardenState.Preview&&Job.Plan.CanExecute);
    internal static Guid ConfirmationId=>_site?.Asking==true?_site.ApprovalId:Job?.State==GardenState.Preview?Job.PreviewId:Guid.Empty;
    internal static bool Owns(int npc)=>Active&&_agent?.whoAmI==npc;
    internal static bool IsTask(string task)=>_task==task;
    internal static string RecoveryProgress=>Job==null?"没有可核实的种植收据":$"本次核实{Job.Cursor}株、已种{Job.Planted}株、实际入包草药{Job.Herbs}、种子{Job.Seeds}；失败目标未计入完成。";
    internal static string Status=>Job?.State==GardenState.Preview?Job.Plan.CanExecute?"种植／采收 · 等待确认":"种植／采收 · 条件不足":
        _site?.Asking==true?"草药采收 · 等你同意":Job?.Plan.Request.Operation==GardenOperation.Plant?"正在种植":"正在采收草药";
    internal static string Begin(Player player,GardenRequest request)
    {
        if(Main.netMode!=NetmodeID.SinglePlayer||!player.active||player.dead||CompanionMemorySystem.Session==Guid.Empty)return "请在单人世界、角色存活时种植或采收。";
        var companion=ChatCoordinator.FindCompanion();if(companion==null)return "请先唤来同伴。";
        if(Vector2.Distance(player.Center,companion.NPC.Center)>12*16)return "请先让同伴靠近你（12格内），再确定种植／采收起点。";
        var origin=companion.NPC.Bottom.ToTileCoordinates();
        var plan=Propose(player,request,new(origin.X,origin.Y));
        return BeginPlan(player,companion,plan);
    }
    internal static string Replan(Player player)
    {
        if(Job==null||!ReferenceEquals(player,_player))return "原种植／采收任务已失效。";
        var companion=ChatCoordinator.FindCompanion();
        if(companion==null||Vector2.Distance(player.Center,companion.NPC.Center)>12*16)return "请先让同伴靠近你，再按原固定目标刷新预览。";
        var old=Job;var total=NearbyChests.Stock(player);
        var seeds=GardenWorld.Crops.ToDictionary(c=>c.Key,c=>total.GetValueOrDefault(c.Value.Seed));
        var plan=old.Plan.Remaining(old.Cursor,seeds,GardenWorld.Scan(old.Plan.Request,old.Plan.Origin));
        var allowed=old.Plan.Targets.Skip(old.Cursor).Select(c=>(c.At,c.Crop)).ToHashSet();
        string notice=BeginPlan(player,companion,plan);_recoveryTargets=allowed;
        return "只重新检查原起点内尚未完成的固定植株，不补采其他位置；需要重新确认。\n"+notice;
    }
    private static string BeginPlan(Player player,global::TerrarAI.Content.NPCs.AIAgentNPC companion,GardenPlan plan)
    {
        _recoveryTargets=null;
        CompanionCommandService.Advance();CompanionAgent.Cancel();CompanionDialogueSystem.Cancel();CompanionInquirySystem.Cancel();
        companion.StopCommand("garden_requested");CompanionConstructionSystem.Hide();StructureWorkshop.Hide();
        _player=player;_agent=companion.NPC;_stamp=CompanionCommandService.Capture();_task=Guid.NewGuid().ToString("N");
        CompanionTaskTracker.Begin(_task,(plan.Request.Operation==GardenOperation.Plant?"种植":"采收")+(plan.Request.Crop is {} c?GardenRequest.Name(c):"草药"));
        SetPlan(plan);return Describe();
    }
    private static GardenPlan Propose(Player p,GardenRequest request,BuildPoint origin)
    {
        var stock=NearbyChests.Stock(p);
        return GardenPlan.Build(request,origin,GardenWorld.Crops.ToDictionary(c=>c.Key,c=>stock.GetValueOrDefault(c.Value.Seed)),GardenWorld.Scan(request,origin));
    }
    private static void SetPlan(GardenPlan plan)
    {
        _site=null;Job=new(plan);_stock=NearbyChests.Snapshot(_player!);
        var bag=NearbyChests.Stock(_player!,false);var total=NearbyChests.Stock(_player!);
        var needs=plan.Request.Crop is {} crop?new Dictionary<GardenCrop,int>{{crop,plan.Request.Count}}:
            plan.Targets.GroupBy(t=>t.Crop).ToDictionary(g=>g.Key,g=>g.Count());
        _seedSources=plan.Request.Operation==GardenOperation.Harvest?"不耗种子；拾取实际掉落的草药和种子":
            needs.Count==0?"暂无可用草药种子，需补种子和适合的空位":string.Join("、",needs.Select(n=>{
                int id=GardenWorld.Crops[n.Key].Seed,owned=bag.GetValueOrDefault(id),boxed=Math.Max(0,total.GetValueOrDefault(id)-owned);
                return $"{Crafting.Name(id)}需{n.Value}（背包{owned}／箱子{boxed}）";}));
        CompanionTaskTracker.Update(_task,CompanionTaskPhase.WaitingForApproval,result:Describe());
        CompanionMemorySystem.Record("garden_preview",_task,null,new{Job.PreviewId,plan=plan.Signature});
    }
    internal static string Confirm(Player p,Guid id)
    {
        if(!Active||!ReferenceEquals(p,_player)||!p.active||p.dead||ChatCoordinator.FindCompanion()?.NPC!=_agent
            ||!_stamp.Matches(CompanionMemorySystem.Session,CompanionCommandService.Revision))return "种植／采收任务已失效。";
        if(_site?.Asking==true)return id==_site.ApprovalId?_site.Approve():"采收目标已变化，请看最新提示。";
        if(Job!.State!=GardenState.Preview||id!=Job.PreviewId)return "请查看最新种植／采收计划后确认。";
        var recoveryStock=_recoveryTargets==null?null:NearbyChests.Stock(p);
        var live=_recoveryTargets==null?Propose(p,Job.Plan.Request,Job.Plan.Origin):GardenPlan.BuildFixed(Job.Plan.Request,Job.Plan.Origin,
            GardenWorld.Crops.ToDictionary(c=>c.Key,c=>recoveryStock!.GetValueOrDefault(c.Value.Seed)),
            GardenWorld.Scan(Job.Plan.Request,Job.Plan.Origin),_recoveryTargets);
        if(DateTimeOffset.UtcNow>=Job.Expires||live.Signature!=Job.Plan.Signature||NearbyChests.Snapshot(p)!=_stock)
        {SetPlan(live);return "植株、库存、箱子、位置或时限变化，已刷新；请查看新计划后再次确认。\n"+Describe();}
        if(!Job.Confirm(id,live.Signature,DateTimeOffset.UtcNow))return "条件还不足，请补齐种子／空位或等待开花后重新发起任务。\n"+Describe();
        _site=new(p,_agent!,live.Origin,NearbyChests.Ids(p),_stamp,_task!);
        CompanionMemorySystem.Record("garden_confirmed",_task,null,new{id,plan=live.Signature});
        return "已确认，开始接近固定目标后逐株种植／采收；可随时停止，已完成部分保留。";
    }
    internal static string Describe()
    {
        if(Job==null)return "没有种植／采收任务。";
        var plan=Job.Plan;
        string targets=string.Join("、",plan.Targets.Select(c=>$"{GardenRequest.Name(c.Crop)}({c.At.X},{c.At.Y})"));
        return $"{(plan.Request.Operation==GardenOperation.Plant?"种植":"采收")}计划：本次{plan.Targets.Count}株（{(plan.Request.Exact?"要求":"上限")}{plan.Request.Count}），起点28格内。\n"+
            "种子来源："+_seedSources+"\n目标："+(targets.Length==0?"未找到":targets)+"\n"+Job.Reason+
            "\n种植优先主背包、再用18格内已关闭未上锁箱子；确认前不扣种子。只采开花草药，幼苗和未开花植株保留，草药与种子按实际进入主背包计数；不会催熟或造土。缺条件时补齐后重新发起；保留采集前询问偏好。";
    }
    public override void PostUpdateWorld()
    {
        if(!Active)return;
        if(_player?.active!=true||_player.dead||ChatCoordinator.FindCompanion()?.NPC!=_agent||!_stamp.Matches(CompanionMemorySystem.Session,CompanionCommandService.Revision))
        {Cancel("角色、同伴、世界或控制指令变化。");return;}
        if(Job!.State!=GardenState.Running||Main.gamePaused||ModContent.GetInstance<CompanionUISystem>().WantsPause)return;
        int before=Job.Cursor;
        try{Job.Tick(_site!);}catch(Exception ex)when(ex is not OutOfMemoryException){Job.Cancel(_site,"种植／采收异常："+ex.Message+"；已完成部分保留。");
            CompanionTaskTracker.SetResult(_task,Job.Reason);CompanionTaskTracker.Close(_task,"garden_failed",0);return;}
        CompanionTaskTracker.Update(_task,Job.Asking?CompanionTaskPhase.WaitingForApproval:CompanionTaskPhase.Executing,result:Job.Reason);
        if(before!=Job.Cursor)CompanionMemorySystem.Record("garden_step",_task,null,new{Job.Cursor,Job.Planted,Job.Herbs,Job.Seeds,Job.Reason});
        if(Job.State is not (GardenState.Completed or GardenState.Failed))return;
        CompanionMemorySystem.Record("garden_audit",_task,null,new{passed=Job.State==GardenState.Completed,Job.Cursor,Job.Planted,Job.Herbs,Job.Seeds,Job.Reason});
        if(Job.State==GardenState.Completed)CompanionTaskTracker.Finish(_task,Job.Reason,Job.Cursor);
        else{CompanionTaskTracker.SetResult(_task,Job.Reason);CompanionTaskTracker.Close(_task,"garden_failed",0);}
        CompanionUISystem.Message(Job.Reason,Job.State==GardenState.Completed?Color.LightGreen:Color.Orange);
    }
    internal static void Cancel(string reason)
    {
        if(!Active)return;Job!.Cancel(_site,reason);CompanionTaskTracker.SetResult(_task,reason+" 已完成部分保留。");
        CompanionTaskTracker.Close(_task,"player_stopped",0);
        CompanionMemorySystem.Record("garden_cancelled",_task,null,new{reason,Job.Cursor,Job.Planted,Job.Herbs,Job.Seeds});
    }
    public override void OnWorldUnload(){Cancel("离开世界。");Job=null;_site=null;_task=null;_player=null;_agent=null;_recoveryTargets=null;}
    public override void Unload(){Job=null;_site=null;_task=null;_player=null;_agent=null;_recoveryTargets=null;}
}
