using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerrarAI.Content.Companion;
using TerrarAI.Content.NPCs;
namespace TerrarAI.Content.Systems;

// Uses the expedition's existing owner, confirmation UI, cancellation and recovery.
public sealed partial class CompanionExpeditionSystem
{
    internal static OreTunnelJob? Tunnel{get;private set;}
    private static NativeOreTunnelSite? _tunnelSite;
    private static CompanionDecision TunnelDecision=>CompanionProtocol.Parse($"{{\"action\":{{\"type\":\"mine\",\"params\":{{\"tileX\":{Tunnel!.Plan.Target.X},\"tileY\":{Tunnel.Plan.Target.Y}}}}}}}");
    private static bool TunnelPermitted=>_approved&&CompanionMemorySystem.Store is {} store&&store.Memory.RuleRevision==_rule&&Main.dayTime==_day&&
        CompanionPolicy.Check(store.Memory.Preferences,TunnelDecision,true,Main.dayTime)==PolicyResult.Allow;
    internal static string BeginTunnel(Player player,OreTunnelRequest request)
    {
        if(Main.netMode!=NetmodeID.SinglePlayer||Main.gameMenu||!player.active||player.dead)return "请在单人世界且角色存活时规划采矿通道。";
        var companion=ChatCoordinator.FindCompanion();var store=CompanionMemorySystem.Store;
        if(companion==null||store==null||CompanionMemorySystem.Session==Guid.Empty)return "请先唤来同伴并等待世界就绪。";
        if(Vector2.Distance(player.Center,companion.NPC.Center)>12*16||!MovementHelper.IsOnGround(companion.NPC)||MovementHelper.InWater(companion.NPC))
            return "请让同伴在你12格内的干燥稳定地面站好，再规划通道。";
        var pick=ToolSelector.FindInventoryTool(player,ToolType.Pickaxe);if(pick==null)return "背包需要一把镐。";
        var start=new TunnelPose((int)(companion.NPC.Center.X/16),(int)MathF.Round(companion.NPC.Bottom.Y/16));
        var skip=new HashSet<Point>();OreTunnelPlan? plan=null;string error="附近没有可规划的矿格，或深度超过本次范围。";
        for(int i=0;i<8;i++)
        {
            var at=GatherTargets.NextOre(companion.NPC,new(start.X,start.FeetY),0,skip,pick.pick,request.Ore,exposedOnly:false);
            if(at is not Point target)break;skip.Add(target);
            plan=OreTunnelPlan.Create(start,DiggingSafety.ReadTunnel(player,target.X,target.Y),(x,y)=>DiggingSafety.ReadTunnel(player,x,y),out error);
            if(plan!=null)break;
        }
        if(plan==null)return error.Length>0?error:"未找到有界、安全且有支撑的通道，未开始挖掘。";
        var decision=CompanionProtocol.Parse($"{{\"action\":{{\"type\":\"mine\",\"params\":{{\"tileX\":{plan.Target.X},\"tileY\":{plan.Target.Y}}}}}}}");
        if(CompanionPolicy.Check(store.Memory.Preferences,decision,day:Main.dayTime)==PolicyResult.Deny)return "当前偏好禁止挖掘，没有创建施工任务。";
        CompanionCommandService.Advance();CompanionAgent.Cancel();CompanionDialogueSystem.Cancel();CompanionInquirySystem.Cancel();
        companion.StopCommand("ore_tunnel_preview");companion.WaitHere(player);
        _agent=companion;_player=player;_stamp=CompanionCommandService.Capture();_rule=store.Memory.RuleRevision;_day=Main.dayTime;_approved=false;
        Job=null;_site=null;_task=Guid.NewGuid().ToString("N");Tunnel=new(plan);_tunnelSite=new(player,companion.NPC,plan,_stamp,()=>TunnelPermitted){Job=Tunnel};
        _ticket=new(CompanionMemorySystem.Session,_task,JsonSerializer.Serialize(plan),DateTimeOffset.UtcNow.AddMinutes(2),_rule,_day);
        ConfirmationId=Guid.NewGuid();CompanionTaskTracker.Begin(_task,"规划通道采矿");UpdateTunnel();
        Log("ore_tunnel_preview",new{plan,ConfirmationId});
        return DescribeTunnel()+"\n默认必须确认：说「同意」或点击同意后才开挖。仅采这一格目标矿；不自动扩展整条矿脉。遇到变化会停止，已完成部分保留。";
    }
    private static string ConfirmTunnel(Player player,Guid id)
    {
        if(!Asking||Tunnel==null||_ticket==null||_tunnelSite==null||_player!=player||id==Guid.Empty||id!=ConfirmationId)return "这次通道确认已失效。";
        var store=CompanionMemorySystem.Store;
        if(store==null||!_tunnelSite.Valid||_agent==null||
            Vector2.Distance(_agent.NPC.Bottom,new Vector2(Tunnel.Plan.Start.X*16+8,Tunnel.Plan.Start.FeetY*16))>8||!_stamp.Matches(CompanionMemorySystem.Session,CompanionCommandService.Revision)||
            !_ticket.Matches(CompanionMemorySystem.Session,_task,JsonSerializer.Serialize(Tunnel.Plan),DateTimeOffset.UtcNow,store.Memory.RuleRevision,Main.dayTime)||
            CompanionPolicy.Check(store.Memory.Preferences,TunnelDecision,true,Main.dayTime)!=PolicyResult.Allow||Tunnel.Check(_tunnelSite).Length>0)
        {Cancel("通道、工具或许可条件已变化，请重新规划确认。");return "通道、工具或许可条件已变化，没有开挖，请重新规划确认。";}
        if(!Tunnel.Approve(_tunnelSite))return "通道地形已变化，没有开挖。";
        _approved=true;_ticket=null;ConfirmationId=Guid.Empty;UpdateTunnel();Log("ore_tunnel_confirmed",new{Tunnel.Plan});
        return "已确认本次通道和目标矿格；将逐格复查后开挖，说停止或等待可立即中止。";
    }
    private static string DescribeTunnel()=>Tunnel==null?"没有采矿通道任务。":"目标："+(GatherTargets.OreLabel(Tunnel.Plan.Target.Type)??"矿物")+"\n"+Tunnel.Plan.Describe+"\n"+Tunnel.Reason+
        $"\n核实移除 {Tunnel.Removed} 块，目标矿格 {Tunnel.VerifiedOre}/1；移除数不等于掉落已入包。\n风险核对：液体、落沙、陷阱、电路、设施、镐力和支撑；不保证发现所有模组危险。";
    private static void UpdateTunnel()=>CompanionTaskTracker.Update(_task,Asking?CompanionTaskPhase.WaitingForApproval:CompanionTaskPhase.Executing,
        action:Status,target:Tunnel?.Plan.Describe,result:DescribeTunnel(),verified:Tunnel?.Removed);
    private static void TickTunnel(AIAgentNPC companion)
    {
        if(!Owns(companion.NPC.whoAmI)||Tunnel==null||_tunnelSite==null)return;
        try
        {
            if(Asking)
            {
                _tunnelSite.Hold();
                if(_ticket?.Matches(CompanionMemorySystem.Session,_task,JsonSerializer.Serialize(Tunnel.Plan),DateTimeOffset.UtcNow,
                    CompanionMemorySystem.Store?.Memory.RuleRevision??-1,Main.dayTime)!=true||!_tunnelSite.Valid)
                    Cancel("通道确认已过期或控制变化，已停止。");
                return;
            }
            int before=Tunnel.Removed;Tunnel.Tick(_tunnelSite.Frame(),_tunnelSite);UpdateTunnel();
            if(before!=Tunnel.Removed)Log("ore_tunnel_progress",new{Tunnel.Removed,Tunnel.VerifiedOre});
            if(Tunnel.Active)return;
            _tunnelSite.Release();companion.WaitHere(_player!);
            if(Tunnel.State==OreTunnelState.Completed)CompanionTaskTracker.Finish(_task,DescribeTunnel(),Tunnel.Removed);
            else {CompanionTaskTracker.SetResult(_task,DescribeTunnel());CompanionTaskTracker.Close(_task,"expedition_failed",Tunnel.Removed);}
            Log("ore_tunnel_finished",new{state=Tunnel.State.ToString(),Tunnel.Reason,Tunnel.Removed,Tunnel.VerifiedOre});
            CompanionUISystem.Message(DescribeTunnel(),Tunnel.State==OreTunnelState.Completed?Color.LightGreen:Color.Orange);
        }
        catch(Exception ex)when(ex is not OutOfMemoryException)
        {Tunnel.Cancel(_tunnelSite,"采矿通道异常："+ex.GetType().Name);CompanionTaskTracker.SetResult(_task,DescribeTunnel());CompanionTaskTracker.Close(_task,"expedition_failed",Tunnel.Removed);}
    }
}
