using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerrarAI.Content.Actions;
using TerrarAI.Content.Companion;
namespace TerrarAI.Content.Systems;

internal sealed class NativeOreTunnelSite(Player player,NPC agent,OreTunnelPlan plan,ControlStamp stamp,Func<bool> permitted):IOreTunnelSite
{
    private MineAction? _mine;
    private (int,int)? _mining;
    private int _miningType=-1;
    private MovementHelper.MovementState _movement;
    internal OreTunnelJob? Job;
    internal bool Valid=>Main.netMode==NetmodeID.SinglePlayer&&!Main.gameMenu&&player.active&&!player.dead&&agent.active&&
        player.whoAmI>=0&&player.whoAmI<Main.player.Length&&ReferenceEquals(Main.player[player.whoAmI],player)&&ReferenceEquals(ChatCoordinator.FindCompanion()?.NPC,agent)&&
        stamp.Matches(CompanionMemorySystem.Session,CompanionCommandService.Revision);
    public DigCell Read(int x,int y)=>DiggingSafety.ReadTunnel(player,x,y);
    internal ExpeditionFrame Frame()
    {
        string danger=Main.npc.Any(n=>n.active&&!n.friendly&&!n.townNPC&&n.damage>0&&Vector2.Distance(n.Center,agent.Center)<8*16)?"附近有敌怪":"";
        for(int x=(int)(agent.position.X/16)-1;x<=(int)((agent.position.X+agent.width)/16)+1;x++)
            for(int y=(int)(agent.position.Y/16)-1;y<=(int)(agent.Bottom.Y/16)+1;y++)
                if(Read(x,y) is {Hazard:true} or {Protected:true})danger="周围出现危险或受保护设施";
        return new((long)Main.GameUpdateCount,agent.Center.X/16.0,agent.Bottom.Y/16.0,Valid,player.active&&!player.dead,
            Main.gamePaused,permitted(),danger);
    }
    internal bool CanWork(int x,int y,int expected)=>Valid&&permitted()&&Job is {State:OreTunnelState.Running}&&Job.Check(this).Length==0&&
        plan.Excavation.Append(plan.Target).Any(c=>c.X==x&&c.Y==y&&c.SameSubstrate(Read(x,y)))&&Read(x,y).Type==expected&&
        Vector2.DistanceSquared(agent.Center,new(x*16+8,y*16+8))<=80*80&&
        (MovementHelper.IsOnGround(agent)||MovementHelper.InWater(agent))&&Frame().Danger.Length==0;
    public ExpeditionStep Mine(int x,int y,int type)
    {
        if(!CanWork(x,y,type))return new(ExpeditionStepState.Failed,"挖掘许可、范围、距离或安全条件已变化，已停止。");
        if(_mining!=(x,y)||_miningType!=type){_mine?.Cancel();_mine=new(new(x,y),singleTile:true,inventoryToolsOnly:true,mayMine:p=>CanWork(p.X,p.Y,type));_mining=(x,y);_miningType=type;}
        var result=_mine!.Tick(new(agent,player));
        int removed=_mine.Mutations.Count(m=>m.X==x&&m.Y==y&&m.BeforeType==type&&m.Removed);
        CompanionDrops.Pickup(player,agent);
        if(result.Status!=AgentActionStatus.Pending){_mine.Cancel();_mine=null;_mining=null;}
        // A native/mod tile hit may strip ordinary grass to dirt without
        // removing the block. Retry only that exact approved cell, with a fresh
        // action and all current guards; it is not a removal or an ore receipt.
        if(result.Status==AgentActionStatus.Failure&&removed==0&&type==Terraria.ID.TileID.Grass&&
            Read(x,y).Type==Terraria.ID.TileID.Dirt&&CanWork(x,y,Terraria.ID.TileID.Dirt))
            return new(ExpeditionStepState.Pending,"草皮已转换为土块；继续核验同一已确认位置，尚未计为移除。");
        return new(result.Status==AgentActionStatus.Pending?ExpeditionStepState.Pending:result.Status==AgentActionStatus.Success?
            ExpeditionStepState.Done:ExpeditionStepState.Failed,result.Message??"",removed);
    }
    public ExpeditionStep Move(TunnelPose pose)
    {
        if(!Valid||!permitted())return new(ExpeditionStepState.Failed,"通道控制已失效。");
        var target=new Vector2(pose.X*16+8,pose.FeetY*16-agent.height/2f);
        var result=MovementHelper.MoveTowards(agent,target,ref _movement,MovementHelper.MovementSettings.Create(2.5f,3));
        if(result.Status==AgentActionStatus.Success&&TerrainRoute.Find(agent,new Vector2(plan.Start.X*16+8,plan.Start.FeetY*16-agent.height/2f),16,3)==null)
            return new(ExpeditionStepState.Failed,"未能核实返回入口的路线，已停止继续开挖。");
        return new(result.Status==AgentActionStatus.Pending?ExpeditionStepState.Pending:result.Status==AgentActionStatus.Success?
            ExpeditionStepState.Done:ExpeditionStepState.Failed,result.Message??"");
    }
    public void Hold(){_mine?.Cancel();_mine=null;_mining=null;MovementHelper.Hold(agent);}
    public void Release(){Hold();_movement.Reset(3);MovementHelper.Stop(agent);}
}
