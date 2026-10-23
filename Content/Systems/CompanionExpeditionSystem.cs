using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using TerrarAI.Content.Actions;
using TerrarAI.Content.Companion;
using TerrarAI.Content.NPCs;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrarAI.Content.Systems;

public sealed partial class CompanionExpeditionSystem:ModSystem
{
    internal static ExpeditionJob? Job{get;private set;}
    private static NativeExpeditionSite? _site;
    private static Player? _player;
    private static AIAgentNPC? _agent;
    private static string? _task;
    private static ControlStamp _stamp;
    private static ApprovalTicket? _ticket;
    private static long _rule;
    private static bool _day,_approved;
    internal static Guid ConfirmationId{get;private set;}
    internal static bool Active=>Job?.Active==true||Tunnel?.Active==true;
    internal static bool Asking=>Active&&_ticket!=null;
    internal static bool Owns(int npc)=>Active&&_agent?.NPC.whoAmI==npc;
    internal static bool IsTask(string task)=>_task==task;
    internal static string Status=>Tunnel!=null?(Asking?"采矿通道 · 等待确认":"开路采矿"):Asking?"挖掘 · 等待确认":Job?.Plan.Digging==true?"向下挖掘":Job?.State==ExpeditionState.Returning?"探路 · 返回起点":"短程探路";
    private static CompanionDecision Decision(ExpeditionPlan plan)=>CompanionProtocol.Parse($"{{\"action\":{{\"type\":\"mine\",\"params\":{{\"tileX\":{plan.StartX},\"tileY\":{plan.StartY}}}}}}}");
    internal static bool Permitted
    {
        get
        {
            if(Tunnel!=null)return TunnelPermitted;
            if(Job?.Plan.Digging!=true)return true;
            var store=CompanionMemorySystem.Store;
            return store!=null&&store.Memory.RuleRevision==_rule&&Main.dayTime==_day&&
                CompanionPolicy.Check(store.Memory.Preferences,Decision(Job.Plan),_approved,Main.dayTime)==PolicyResult.Allow;
        }
    }
    internal static string Begin(Player player,ExpeditionRequest request)
    {
        if(Main.netMode!=NetmodeID.SinglePlayer||!player.active||player.dead||CompanionMemorySystem.Session==Guid.Empty)return "请在单人世界、角色存活时安排挖掘或探路。";
        var companion=ChatCoordinator.FindCompanion();if(companion==null)return "请先唤来同伴。";
        if(Vector2.Distance(player.Center,companion.NPC.Center)>12*16)return "请先让同伴靠近你（12格内），再确定挖掘或探路起点。";
        if(!MovementHelper.IsOnGround(companion.NPC)||MovementHelper.InWater(companion.NPC))return "请先让同伴站在干燥、稳定的地面上。";
        var plan=ExpeditionPlan.Create(request,(int)(companion.NPC.Center.X/16),(int)MathF.Round(companion.NPC.Bottom.Y/16),Main.worldSurface,
            player.direction,Main.maxTilesX,Main.maxTilesY,out string error);
        if(plan==null)return error;
        if(plan.Digging&&plan.Distance==0)return "同伴已经在地下层，无需继续开挖。";
        var store=CompanionMemorySystem.Store;if(store==null)return "世界记忆尚未就绪。";
        var permission=plan.Digging?CompanionPolicy.Check(store.Memory.Preferences,Decision(plan),day:Main.dayTime):PolicyResult.Allow;
        if(permission==PolicyResult.Deny)return "当前偏好禁止挖掘，没有开始。";
        if(plan.Digging&&plan.Distance>0&&ToolSelector.FindInventoryTool(player,ToolType.Pickaxe)==null)return "背包中需要一把镐，没有开始挖掘。";
        CompanionCommandService.Advance();CompanionAgent.Cancel();CompanionDialogueSystem.Cancel();CompanionInquirySystem.Cancel();
        companion.StopCommand("expedition_requested");companion.WaitHere(player);
        _agent=companion;_player=player;_stamp=CompanionCommandService.Capture();_rule=store.Memory.RuleRevision;_day=Main.dayTime;_approved=false;
        _task=Guid.NewGuid().ToString("N");Tunnel=null;_tunnelSite=null;Job=new(plan);_site=new(player,companion.NPC,plan,_stamp);
        _ticket=permission==PolicyResult.Ask?new(CompanionMemorySystem.Session,_task,JsonSerializer.Serialize(plan),DateTimeOffset.UtcNow.AddMinutes(2),_rule,_day):null;
        ConfirmationId=_ticket!=null?Guid.NewGuid():Guid.Empty;
        CompanionTaskTracker.Begin(_task,request.Mode==ExpeditionMode.Underground?"挖到地下层":request.Mode==ExpeditionMode.Tunnel?"往下挖通道":"去前面探探");
        Update();Log("expedition_started",new{plan,asking=Asking});
        return plan.Describe+"。"+(Asking?"当前偏好要求采集确认，请同意这次有界竖井任务后开始。":"开始执行；说停止、等待或回来可中止。")+
            (plan.Digging?"遇到建筑、电路、液体、落沙、危险落点或镐力不足会停止；掉落留在现场，移除数不等于背包收获。":"不挖地形，沿可走路线观察；遇到近处危险会停下，不保证排除所有危险。")+
            "结束后原地等待，报告实际发现。";
    }
    internal static string Confirm(Player player,Guid id)
    {
        if(Tunnel!=null)return ConfirmTunnel(player,id);
        if(!Asking||_ticket==null||Job==null||_player!=player||id==Guid.Empty||id!=ConfirmationId)return "这次挖掘确认已失效。";
        var store=CompanionMemorySystem.Store;
        if(store==null||!_stamp.Matches(CompanionMemorySystem.Session,CompanionCommandService.Revision)||
            !_ticket.Matches(CompanionMemorySystem.Session,_task,JsonSerializer.Serialize(Job.Plan),DateTimeOffset.UtcNow,store.Memory.RuleRevision,Main.dayTime)||
            CompanionPolicy.Check(store.Memory.Preferences,Decision(Job.Plan),true,Main.dayTime)!=PolicyResult.Allow)
        {Cancel("挖掘许可过期或规则变化，请重新安排任务。");return "挖掘许可过期或规则变化，请重新安排任务。";}
        _approved=true;_ticket=null;ConfirmationId=Guid.Empty;Update();return "已同意本次三格宽、限定深度的竖井任务；每个方块仍需通过保护检查。";
    }
    internal static void Cancel(string reason)
    {
        if(Tunnel?.Active==true){Tunnel.Cancel(_tunnelSite,reason);CompanionTaskTracker.SetResult(_task,Describe());CompanionTaskTracker.Close(_task,"player_stopped",Tunnel.Removed);Log("ore_tunnel_cancelled",new{reason,Tunnel.Removed});}
        else if(Active){Job!.Cancel(_site,reason);CompanionTaskTracker.SetResult(_task,Describe());CompanionTaskTracker.Close(_task,"player_stopped",Job.Removed);Log("expedition_cancelled",new{reason,Job.Removed});}
        _site?.Release();_tunnelSite?.Release();_ticket=null;ConfirmationId=Guid.Empty;
    }
    private static void Update()=>CompanionTaskTracker.Update(_task,Asking?CompanionTaskPhase.WaitingForApproval:CompanionTaskPhase.Executing,
        action:Status,target:Job?.Plan.Describe,result:Describe(),verified:Job?.Removed);
    internal static string Describe()=>Tunnel!=null?DescribeTunnel():Job==null?"没有挖掘或探路任务。":Job.Plan.Describe+"\n"+Job.Reason+$"\n核实移除{Job.Removed}块，清空{Job.ClearedRows}层；掉落留在现场。\n"+
        Job.Survey.Report(_player?.Center.X/16.0??0,_player?.Center.Y/16.0??0);
    internal static void TickActive(AIAgentNPC companion)
    {
        if(Tunnel!=null){TickTunnel(companion);return;}
        if(!Owns(companion.NPC.whoAmI)||_site==null||Job==null)return;
        try
        {
            if(Asking)
            {
                _site.Hold();
                if(_ticket?.Matches(CompanionMemorySystem.Session,_task,JsonSerializer.Serialize(Job.Plan),DateTimeOffset.UtcNow,
                    CompanionMemorySystem.Store?.Memory.RuleRevision??-1,Main.dayTime)!=true)Cancel("挖掘许可已过期，请重新安排任务。");
                return;
            }
            int before=Job.Removed;Job.Tick(_site.Frame(),_site);
            if(Job.Removed!=before)Log("expedition_tile_removed",new{Job.Removed,Job.ClearedRows,tile=_site.LastRemoval});
            Update();if(Job.Active)return;
            _ticket=null;ConfirmationId=Guid.Empty;_site.Release();companion.WaitHere(_player!);
            if(Job.State==ExpeditionState.Completed)CompanionTaskTracker.Finish(_task,Describe(),Job.Removed);
            else {CompanionTaskTracker.SetResult(_task,Describe());CompanionTaskTracker.Close(_task,Job.State==ExpeditionState.Cancelled?"player_stopped":"expedition_failed",Job.Removed);}
            Log("expedition_finished",new{state=Job.State.ToString(),Job.Reason,Job.Removed,Job.ClearedRows,findings=Job.Survey.Findings});
            CompanionUISystem.Message(Describe(),Job.State==ExpeditionState.Completed?Color.LightGreen:Color.Orange);
        }
        catch(Exception ex)when(ex is not OutOfMemoryException){Job?.Cancel(_site,"探路或挖掘异常："+ex.GetType().Name);_site?.Release();_tunnelSite?.Release();_ticket=null;ConfirmationId=Guid.Empty;
            CompanionTaskTracker.SetResult(_task,Describe());CompanionTaskTracker.Close(_task,"expedition_failed",Job?.Removed??0);
            CompanionUISystem.Message(Describe(),Color.Orange);}
    }
    public override void PostUpdateWorld()
    {
        if(Active&&(_player?.active!=true||_player.dead||_agent?.NPC.active!=true||!ReferenceEquals(ChatCoordinator.FindCompanion(),_agent)||
            !_stamp.Matches(CompanionMemorySystem.Session,CompanionCommandService.Revision)))Cancel("角色、同伴、世界或指令已变化，任务停止。");
    }
    public override void OnWorldUnload(){Cancel("离开世界，任务停止。");Reset();}
    public override void Unload()=>Reset();
    private static void Reset(){Tunnel=null;_tunnelSite=null;Job=null;_site=null;_player=null;_agent=null;_task=null;_ticket=null;ConfirmationId=Guid.Empty;}
    private static void Log(string kind,object data)
    {try{CompanionMemorySystem.Record(kind,_task,null,data);}catch(Exception ex)when(ex is not OutOfMemoryException){/* Logging cannot repeat a world edit. */}}
}

internal sealed class NativeExpeditionSite(Player player,NPC agent,ExpeditionPlan plan,ControlStamp stamp):IExpeditionSite
{
    private HellevatorAction? _mine;
    private (int X,int Y,int Type)? _mining;
    private MovementHelper.MovementState _movement;
    private Vector2? _destination;
    private readonly TerrariaLocationSource _locations=new(player);
    internal TileMutation? LastRemoval{get;private set;}
    private bool Valid=>Main.netMode==NetmodeID.SinglePlayer&&!Main.gameMenu&&player.active&&!player.dead&&agent.active&&
        ReferenceEquals(Main.player[player.whoAmI],player)&&ReferenceEquals(ChatCoordinator.FindCompanion()?.NPC,agent)&&
        stamp.Matches(CompanionMemorySystem.Session,CompanionCommandService.Revision);
    private static bool Trap(int type)=>type is TileID.Spikes or TileID.WoodenSpikes or TileID.Traps or TileID.Boulder or TileID.Explosives;
    private static bool Falling(int type)=>type is TileID.Sand or TileID.Silt or TileID.Slush or TileID.Ebonsand or TileID.Crimsand or TileID.Pearlsand;
    private static bool Threat(NPC n)=>n.active&&!n.friendly&&!n.townNPC&&n.damage>0&&n.type>=NPCID.None&&n.type<NPCID.Sets.CountsAsCritter.Length&&
        !NPCID.Sets.CountsAsCritter[n.type]&&!NPCID.Sets.ActsLikeTownNPC[n.type]&&!NPCID.Sets.IsTownPet[n.type];
    private static bool Solid(int x,int y)
    {var t=Framing.GetTileSafely(x,y);return t.HasTile&&!t.IsActuated&&Main.tileSolid[t.TileType];}
    private static string? Hazard(Tile t)=>t.LiquidAmount>0?(t.LiquidType==LiquidID.Lava?"岩浆":t.LiquidType==LiquidID.Water?"积水":"危险液体"):
        t.HasTile&&!t.IsActuated&&Trap(t.TileType)?"陷阱／尖刺／滚石":t.RedWire||t.BlueWire||t.GreenWire||t.YellowWire?"电路线":null;
    public DigCell Read(int x,int y)=>DiggingSafety.Read(player,x,y);
    public DigBand Band(int row)
    {
        var cells=new List<DigCell>();
        for(int x=plan.StartX-2;x<=plan.StartX+2;x++)for(int y=row-3;y<=row+2;y++)cells.Add(Read(x,y));
        bool landing=false;
        for(int y=row+1;y<=Math.Min(row+12,plan.EndY);y++)if(Enumerable.Range(plan.StartX-1,3).All(x=>Solid(x,y))){landing=true;break;}
        return new(cells,landing,"附近有液体、落沙、陷阱、电路，或下方12格内／任务深度内没有完整安全支撑。");
    }
    public ExpeditionStep Mine(int x,int y,int expectedType)
    {
        LastRemoval=null;
        bool Guard(Point at)=>Valid&&CompanionExpeditionSystem.Permitted&&plan.ContainsDig(at.X,at.Y)&&Read(at.X,at.Y).Type==expectedType&&Band(at.Y).Problem(plan,at.Y).Length==0;
        if(!Guard(new(x,y)))return new(ExpeditionStepState.Failed,"挖掘前工具、目标或周围保护条件已变化。");
        if(_mining!=(x,y,expectedType)){_mine?.Cancel();_mine=new(plan.StartX,new(x,y),Guard);_mining=(x,y,expectedType);}
        var result=_mine!.Tick(new(agent,player));
        int removed=_mine.Mutations.Count(m=>m.X==x&&m.Y==y&&m.Removed);
        LastRemoval=_mine.Mutations.LastOrDefault(m=>m.X==x&&m.Y==y&&m.Removed);
        if(result.Status!=AgentActionStatus.Pending){_mine.Cancel();_mine=null;_mining=null;}
        var after=Read(x,y);
        if(result.Status==AgentActionStatus.Failure&&after.Type>=0&&after.Type!=expectedType&&after.Mineable&&!after.Protected&&!after.Hazard)
            return new(ExpeditionStepState.Pending,"方块类型已变化，将重新核实后继续。",removed);
        return new(result.Status==AgentActionStatus.Pending?ExpeditionStepState.Pending:result.Status==AgentActionStatus.Success?ExpeditionStepState.Done:ExpeditionStepState.Failed,
            result.Message??"挖掘未完成。",removed);
    }
    public ExpeditionStep Move(double centerX,double feetY)
    {
        if(!Valid)return new(ExpeditionStepState.Failed,"角色或控制指令已变化。");
        if(_destination==null)
        {
            // Use the existing route search to choose an actual nearby standing
            // destination on hills, then keep that route intact during jumps.
            bool home=Math.Abs(centerX-plan.CenterX)<0.1;
            int span=home?3:16;
            var candidates=Enumerable.Range((int)feetY-span,span*2+1)
                .OrderBy(y=>Math.Abs(y-agent.Bottom.Y/16f)).Select(y=>new Vector2((float)centerX*16,y*16-agent.height/2f))
                .Where(p=>plan.ContainsActor(p.X/16,(p.Y+agent.height/2f)/16))
                .Where(p=>!Collision.SolidCollision(p-new Vector2(agent.width/2f,agent.height/2f),agent.width,agent.height)&&
                    Collision.TileCollision(p-new Vector2(agent.width/2f,agent.height/2f),new Vector2(0,1),agent.width,agent.height).Y<0.5f);
            foreach(var target in candidates)
                if(TerrainRoute.Find(agent,target,20,3.5f)!=null){_destination=target;break;}
            if(_destination==null)return new(ExpeditionStepState.Failed,"没有可走的落脚点，不会挖墙或传送。");
        }
        var result=MovementHelper.MoveTowards(agent,_destination.Value,ref _movement,MovementHelper.MovementSettings.Create(3.5f,20));
        return new(result.Status==AgentActionStatus.Pending?ExpeditionStepState.Pending:result.Status==AgentActionStatus.Success?ExpeditionStepState.Done:ExpeditionStepState.Failed,result.Message??"");
    }
    public void Hold(){_mine?.Cancel();_mine=null;_mining=null;MovementHelper.Hold(agent);}
    public void Release(){Hold();_movement.Reset(20);_destination=null;MovementHelper.Stop(agent);}
    internal ExpeditionFrame Frame()
    {
        string danger="";
        // Hostile NPC proximity is a reason to stop scouting, not an implicit
        // command to fight. Survey records the observed entity separately.
        if(Main.npc.Any(n=>Threat(n)&&n.whoAmI!=agent.whoAmI&&
            Vector2.Distance(n.Center,agent.Center)<=6*16&&Collision.CanHitLine(agent.Center,1,1,n.Center,1,1)))danger="近处有敌怪";
        if(!plan.Digging)
        {
            int direction=_destination is Vector2 d?Math.Sign(d.X-agent.Center.X):plan.Direction;
            int x=(int)(agent.Center.X/16)+direction*2,y=(int)(agent.Bottom.Y/16);
            if(WorldGen.InWorld(x,y,12)&&!Enumerable.Range(y,13).Any(yy=>Solid(x,yy)))danger="前方有超过12格的深坑";
            for(int xx=(int)(agent.position.X/16)-1;xx<=(int)(agent.Right.X/16)+1;xx++)
                for(int yy=(int)(agent.position.Y/16)-1;yy<=(int)(agent.Bottom.Y/16)+1;yy++)
                    if(DangerAt(xx,yy) is MapLocation hazard&&Visible(agent.Center,hazard))danger=hazard.Name;
        }
        return new((long)Main.GameUpdateCount,agent.Center.X/16.0,agent.Bottom.Y/16.0,Valid,player.active&&!player.dead,Main.gamePaused,CompanionExpeditionSystem.Permitted,danger);
    }
    private static bool Visible(Vector2 observer,MapLocation point)
    {
        if(Vector2.DistanceSquared(observer,new((float)point.CenterX*16,(float)point.CenterY*16))>ExpeditionSurvey.Radius*ExpeditionSurvey.Radius*256)return false;
        if(Collision.CanHitLine(observer,1,1,new((float)point.CenterX*16,(float)point.CenterY*16),1,1))return true;
        if(point.Kind is not (LocationKind.Ore or LocationKind.Danger)||point.Type<0)return false;
        foreach(var delta in new[]{new Point(-1,0),new Point(1,0),new Point(0,-1),new Point(0,1)})
        {
            int x=point.X+delta.X,y=point.Y+delta.Y;var tile=Framing.GetTileSafely(x,y);
            if((!tile.HasTile||!Main.tileSolid[tile.TileType]||tile.IsActuated)&&Collision.CanHitLine(observer,1,1,new Vector2(x*16+8,y*16+8),1,1))return true;
        }
        return false;
    }
    private static MapLocation? DangerAt(int x,int y)
    {
        if(!WorldGen.InWorld(x,y,12))return null;var t=Framing.GetTileSafely(x,y);string? name=Hazard(t);
        return name==null?null:new(LocationKind.Danger,$"danger:{x}:{y}:{name}",name,x,y,t.HasTile?t.TileType:-1,Icon:ItemID.Sign);
    }
    private static MapLocation Enemy(NPC n)=>new(LocationKind.Danger,$"enemy:{n.whoAmI}:{n.type}:{n.GetGlobalNPC<CompanionEnemyIdentity>().Serial}","敌怪："+n.TypeName,
        (int)(n.Center.X/16),(int)(n.Center.Y/16),n.type,Icon:ItemID.Sign);
    private static MapLocation? DropAt(int x,int y)=>WorldGen.InWorld(x,y,12)&&!Enumerable.Range(y,13).Any(yy=>Solid(x,yy))?
        new(LocationKind.Danger,$"drop:{x}:{y}","深坑（下方超过12格无支撑）",x,y,Icon:ItemID.Sign):null;
    private static bool DangerValid(MapLocation p)
    {
        if(p.Identity.StartsWith("drop:",StringComparison.Ordinal))return DropAt(p.X,p.Y)?.Identity==p.Identity;
        if(p.Identity.StartsWith("enemy:",StringComparison.Ordinal))return Main.npc.Any(n=>Threat(n)&&Enemy(n).Identity==p.Identity&&
            Math.Abs(n.Center.X/16-p.CenterX)<=2&&Math.Abs(n.Center.Y/16-p.CenterY)<=2);
        return DangerAt(p.X,p.Y)?.Identity==p.Identity;
    }
    public IEnumerable<ExpeditionObservation> Observe()
    {
        if(!Valid)yield break;
        var observer=agent.Center;
        ExpeditionObservation Seen(MapLocation p)=>new(p,observer.X/16.0,observer.Y/16.0,Visible(observer,p));
        if(!plan.Digging)
        {
            int direction=_destination is Vector2 d?Math.Sign(d.X-agent.Center.X):plan.Direction;
            if(DropAt((int)(agent.Center.X/16)+direction*2,(int)(agent.Bottom.Y/16)) is MapLocation drop)yield return Seen(drop);
        }
        foreach(var box in NearbyChests.Locations(observer.X/16.0,observer.Y/16.0,ExpeditionSurvey.Radius))yield return Seen(_locations.ChestLocation(box));
        int cx=(int)(observer.X/16),cy=(int)(observer.Y/16);
        for(int x=cx-12;x<=cx+12;x++)for(int y=cy-12;y<=cy+12;y++)
        {
            if(!WorldGen.InWorld(x,y,12))continue;var tile=Framing.GetTileSafely(x,y);
            if(tile.HasTile&&!tile.IsActuated&&tile.TileType<TileID.Sets.Ore.Length&&TileID.Sets.Ore[tile.TileType])
                yield return Seen(_locations.OreLocation(x,y,tile.TileType));
            if(DangerAt(x,y) is MapLocation hazard)yield return Seen(hazard);
        }
        foreach(var npc in Main.npc.Where(n=>Threat(n)&&n.whoAmI!=agent.whoAmI&&Vector2.Distance(n.Center,observer)<=12*16))yield return Seen(Enemy(npc));
    }
    public bool Mark(ExpeditionObservation finding)
    {
        if(!Valid||!Visible(agent.Center,finding.Location))return false;
        bool Physical(MapLocation p)=>p.Kind==LocationKind.Danger?DangerValid(p):_locations.Valid(p,observed:true);
        bool marked=MapLocationSystem.RememberObserved(player,finding.Location,Physical);
        if(marked)CompanionUISystem.Message("沿途发现："+MapLocationSystem.Describe(player,finding.Location)+"已标记。",Color.LightBlue);
        return marked;
    }
}
