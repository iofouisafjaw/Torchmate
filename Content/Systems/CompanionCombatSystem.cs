using System;
using System.Linq;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TerrarAI.Content.Actions;
using TerrarAI.Content.Companion;
using TerrarAI.Content.NPCs;
using Terraria;
using Terraria.Audio;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using Vec=System.Numerics.Vector2;

namespace TerrarAI.Content.Systems;

// Entity generation, rather than an NPC array index, identifies a combat target.
public sealed class CompanionEnemyIdentity:GlobalNPC
{
    public override bool InstancePerEntity=>true;
    private static long _serial;
    internal long Serial{get;private set;}
    public override void SetDefaults(NPC entity)=>Serial=Interlocked.Increment(ref _serial);
}

public sealed class CompanionCombatSystem:ModSystem
{
    internal static CombatJob? Job{get;private set;}
    private static CombatJob? _automatic;
    private static NativeCombatSite? _site;
    private static Player? _player;
    private static NPC? _agent;
    private static ControlStamp _stamp;
    private static string? _task;
    private static readonly DefenseGate _gate=new();
    private static readonly AttackCadence _cadence=new();
    internal static bool Active=>Job?.State==CombatState.Running;
    internal static bool Fighting=>_site?.Fighting==true;
    internal static bool Owns(int npc)=>Active&&_agent?.whoAmI==npc;
    internal static bool IsTask(string task)=>_task==task;
    internal static string Status=>Active?Job!.Mode==CombatMode.Protect?"保护玩家":"清理附近小怪":Fighting?"自动反击":"跟随中";
    internal static CombatProgress Progress()=>new(NPC.downedBoss1,NPC.downedBoss2,NPC.downedBoss3,Main.hardMode,
        NPC.downedMechBossAny,NPC.downedPlantBoss,NPC.downedGolemBoss,NPC.downedMoonlord);
    internal static string Begin(Player player,string mode)
    {
        if(mode is not ("protect" or "clear" or "off"))return "战斗指令需为保护、清怪或停止攻击。";
        if(Main.netMode!=NetmodeID.SinglePlayer||!player.active||player.dead||CompanionMemorySystem.Session==Guid.Empty)
            return "请在单人世界、角色存活时安排护卫。";
        var companion=ChatCoordinator.FindCompanion();if(companion==null)return "请先唤来同伴。";
        CompanionCommandService.Advance();CompanionAgent.Cancel();CompanionDialogueSystem.Cancel();CompanionInquirySystem.Cancel();
        companion.StopCommand("combat_requested");companion.ResumeFollowing(player);
        if(mode=="off"){Suppress();return "已停止攻击，继续跟随；说“保护我”或“继续跟随”可恢复反击。";}
        _gate.Resume();_player=player;_agent=companion.NPC;_stamp=CompanionCommandService.Capture();
        _site=new(player,_agent,_stamp);Job=new(mode=="protect"?CombatMode.Protect:CombatMode.Clear,Point(player.Center));
        _task=Guid.NewGuid().ToString("N");
        CompanionTaskTracker.Begin(_task,mode=="protect"?"保护我":"清理附近小怪");
        CompanionTaskTracker.Update(_task,CompanionTaskPhase.Executing,action:Status,result:Describe());
        Log("combat_started",new{mode,Job.Origin,range=mode=="protect"?CombatRules.ProtectRadius:CombatRules.ClearRadius});
        return mode=="protect"?"开始保护你：跟随并近身处理靠近的小怪，不攻击城镇NPC、宠物和Boss；说停止或等待可中止。":
            "开始清理任务起点附近24格的小怪，最多持续60秒；不追出范围，不攻击城镇NPC、宠物和Boss。";
    }
    internal static void ResumeAutomatic()=>_gate.Resume();
    internal static void Suppress(){Cancel("已停止攻击。");_gate.Stop();}
    internal static void Cancel(string reason)
    {
        if(Active)
        {Job!.Cancel(_site,reason);CompanionTaskTracker.SetResult(_task,reason);CompanionTaskTracker.Close(_task,"player_stopped",Job.Hits);Log("combat_cancelled",new{reason,Job.Hits,Job.Kills});}
        _automatic?.Cancel(_site,reason);_automatic=null;_site?.Release();_site=null;
    }
    internal static void TickActive(AIAgentNPC companion)
    {
        if(!Owns(companion.NPC.whoAmI)||_site==null)return;
        Tick(Job!,_site);
        CompanionTaskTracker.Update(_task,CompanionTaskPhase.Executing,action:Status,target:Job!.Target?.ToString()??"",result:Describe());
        if(Job.State==CombatState.Running)return;
        _site.Release();
        if(Job.State==CombatState.Completed)CompanionTaskTracker.Finish(_task,Job.Reason,Job.Hits);
        else {CompanionTaskTracker.SetResult(_task,Job.Reason);CompanionTaskTracker.Close(_task,Job.State==CombatState.Failed?"combat_failed":"player_stopped",Job.Hits);}
        Log("combat_finished",new{state=Job.State.ToString(),Job.Hits,Job.Kills,Job.DamageDealt,Job.Remaining,Job.Reason});
        CompanionUISystem.Message(Job.Reason,Job.State==CombatState.Completed?Color.LightGreen:Color.Orange);
    }
    internal static bool TickAutomatic(AIAgentNPC companion,Player player)
    {
        if(Active||!_gate.Allows(TerrarAI_Config.Get().EnableAutomaticDefense,companion.Mode==CompanionMode.Follow,companion.InterfaceState==AgentState.Idle,
            CompanionAgent.Running||CompanionDialogueSystem.IsBusy,player.active&&!player.dead))
        { _automatic?.Cancel(_site,"暂不自动反击。");_automatic=null;if(!Active)_site?.Release();return false; }
        if(_automatic==null||!ReferenceEquals(_player,player)||!ReferenceEquals(_agent,companion.NPC)||!_stamp.Matches(CompanionMemorySystem.Session,CompanionCommandService.Revision))
        {
            _site?.Release();_player=player;_agent=companion.NPC;_stamp=CompanionCommandService.Capture();
            _site=new(player,companion.NPC,_stamp);_automatic=new(CombatMode.Automatic,Point(player.Center));
        }
        return Tick(_automatic,_site!);
    }
    private static bool Tick(CombatJob job,NativeCombatSite site)
    {
        try
        {
            int before=job.Hits;
            bool owns=job.Tick(site.Frame(job),site);
            if(job.Hits!=before)
                Log("combat_hit",new{mode=job.Mode.ToString(),job.LastHit,job.Hits,job.Kills,job.DamageDealt,job.Reason},job!=Job);
            return owns;
        }
        catch(Exception ex)when(ex is not OutOfMemoryException)
        {job.Cancel(site,"战斗执行异常："+ex.GetType().Name);
            if(ReferenceEquals(job,Job)){CompanionTaskTracker.SetResult(_task,Describe());CompanionTaskTracker.Close(_task,"combat_failed",job.Hits);}
            return false;}
    }
    internal static string Describe()=>Job==null?"跟随时会对靠近的小怪近身反击。":
        (Job.Mode==CombatMode.Protect?"保护玩家":"清理附近小怪")+$" · {Job.Reason}\n实际命中{Job.Hits}次、击杀{Job.Kills}个、造成{Job.DamageDealt}伤害。"+
        $"\n当前进度基础伤害上限{CombatRules.DamageCap(Progress())}，最短48帧一次；仅近身、视线畅通且允许攻击的敌怪。";
    internal static string AutomaticDescription()=>_automatic==null?"正在留意靠近的小怪。":
        $"正在近身反击靠近的小怪。{_automatic.Reason} 本次反击已核实命中{_automatic.Hits}次、击杀{_automatic.Kills}个。";
    internal static bool ReserveStrike(int cooldown)
        =>_cadence.Reserve((long)Main.GameUpdateCount,cooldown);
    private static void Log(string kind,object data,bool automatic=false)
    {try{CompanionMemorySystem.Record(kind,automatic?null:_task,null,data);}catch(Exception ex)when(ex is not OutOfMemoryException){/* An audit failure cannot repeat an already applied hit. */}}
    internal static Vec Point(Vector2 p)=>new(p.X,p.Y);
    public override void PostUpdateWorld()
    {
        if(Active&&(_player?.active!=true||_player.dead||_agent?.active!=true||!_stamp.Matches(CompanionMemorySystem.Session,CompanionCommandService.Revision)||!ReferenceEquals(ChatCoordinator.FindCompanion()?.NPC,_agent)))
            Cancel("角色、同伴、世界或控制指令已变化，战斗已停止。");
    }
    public override void OnWorldUnload(){Cancel("离开世界，战斗已停止。");Job=null;_player=null;_agent=null;_task=null;_gate.Resume();_cadence.Reset();}
    public override void Unload(){Job=null;_automatic=null;_site=null;_player=null;_agent=null;_task=null;_gate.Resume();_cadence.Reset();}
    internal static void Draw(NPC npc,SpriteBatch batch,Vector2 screen,Color color)=>_site?.Draw(npc,batch,screen,color);
}

internal sealed class NativeCombatSite(Player player,NPC agent,ControlStamp stamp):ICombatSite
{
    private readonly FollowNavigation _follow=new();
    private MovementHelper.MovementState _movement;
    private EnemyKey? _moving;
    private Vector2 _destination;
    private int _age;
    private bool _following;
    private CombatMode _mode;
    private Vec _origin;
    private readonly Item _fallback=new(ItemID.WoodenSword);
    private Item _weapon=new(ItemID.WoodenSword);
    private long _swingUntil;
    internal bool Fighting{get;private set;}
    private bool Valid=>Main.netMode==NetmodeID.SinglePlayer&&!Main.gameMenu&&player.active&&!player.dead&&agent.active&&
        ReferenceEquals(Main.player[player.whoAmI],player)&&ReferenceEquals(ChatCoordinator.FindCompanion()?.NPC,agent)&&stamp.Matches(CompanionMemorySystem.Session,CompanionCommandService.Revision);
    private static EnemyKey Key(NPC npc)=>new(npc.whoAmI,npc.type,npc.GetGlobalNPC<CompanionEnemyIdentity>().Serial);
    private NPC? Entity(EnemyKey key)=>key.Slot>=0&&key.Slot<Main.maxNPCs&&Main.npc[key.Slot] is NPC n&&Key(n)==key?n:null;
    private static bool Set(bool[] flags,int type)=>type<0||type>=flags.Length||flags[type];
    private static (bool Town,bool Critter,bool Boss) Protected(NPC npc)
    {
        bool town=false,critter=false,boss=false;int visited=0;var current=npc;
        while(true)
        {
            int type=current.type;
            town|=current.friendly||current.townNPC||Set(NPCID.Sets.ActsLikeTownNPC,type)||Set(NPCID.Sets.IsTownPet,type)||Set(NPCID.Sets.IsTownSlime,type)||Set(NPCID.Sets.TownCritter,type);
            critter|=Set(NPCID.Sets.CountsAsCritter,type)||type==NPCID.TargetDummy;
            boss|=current.boss||Set(NPCID.Sets.ShouldBeCountedAsBoss,type)||type==NPCID.DungeonGuardian;
            if(current.realLife<0||current.realLife==current.whoAmI)break;
            if(++visited>16||current.realLife>=Main.maxNPCs||!Main.npc[current.realLife].active){boss=true;break;}
            current=Main.npc[current.realLife];
        }
        return(town,critter,boss);
    }
    private CombatTarget Snapshot(NPC npc,bool hooks=true)
    {
        var guarded=Protected(npc);
        var target=new CombatTarget(Key(npc),CompanionCombatSystem.Point(npc.Center),npc.width,npc.height,npc.life,npc.damage,
            npc.active,npc.friendly,npc.townNPC||guarded.Town,guarded.Critter,guarded.Boss,npc.immortal,npc.dontTakeDamage,npc.CanBeChasedBy(agent,ignoreDontTakeDamage:true),
            Immune:npc.immune[255]>0);
        if(!CombatRules.Eligible(target))return target;
        return target with{HitAllowed=!hooks||NPCLoader.CanHitNPC(agent,npc),Visible=Collision.CanHitLine(agent.Center,1,1,npc.Center,1,1)};
    }
    internal CombatFrame Frame(CombatJob job)
    {
        _mode=job.Mode;_origin=job.Origin;
        _weapon=ToolSelector.FindCombatWeapon(player)??_fallback;
        var playerAt=CompanionCombatSystem.Point(player.Center);var agentAt=CompanionCombatSystem.Point(agent.Center);
        var context=new CombatFrame((long)Main.GameUpdateCount,playerAt,agentAt,Array.Empty<CombatTarget>(),CompanionCombatSystem.Progress());
        return new((long)Main.GameUpdateCount,CompanionCombatSystem.Point(player.Center),CompanionCombatSystem.Point(agent.Center),
            Main.npc.Where(n=>n!=null&&n.active&&n.whoAmI!=agent.whoAmI&&
                CombatRules.InScope(job.Mode,job.Origin,context,new CombatTarget(default,CompanionCombatSystem.Point(n.Center))))
                .Select(n=>Snapshot(n)).ToArray(),CompanionCombatSystem.Progress(),
            ReferenceEquals(_weapon,_fallback)?0:_weapon.damage,_weapon.useTime,Valid,player.active&&!player.dead,Main.gamePaused);
    }
    public CombatTarget? Read(EnemyKey key)=>Entity(key) is NPC n?Snapshot(n):null;
    public CombatMove Approach(EnemyKey key,out string message)
    {
        message="正在接近小怪。";
        if(!Valid||Entity(key) is not NPC target||!CombatRules.Eligible(Snapshot(target)))return CombatMove.Failed;
        BeginFight();_age++;
        bool grounded=MovementHelper.IsOnGround(agent)||MovementHelper.InWater(agent);
        if(_moving!=key||grounded&&_age>=30&&Vector2.DistanceSquared(target.Center,_destination)>48*48)
        {_movement.Reset(CombatRules.Reach-8);_moving=key;_destination=target.Center;_age=0;}
        var result=MovementHelper.MoveTowards(agent,_destination,ref _movement,MovementHelper.MovementSettings.Create(3.5f,CombatRules.Reach-8));
        message="护卫接敌 · "+result.Message;
        return result.Status==AgentActionStatus.Failure?CombatMove.Failed:result.Status==AgentActionStatus.Success?CombatMove.Reached:CombatMove.Pending;
    }
    public CombatHit Strike(EnemyKey key,int damage,int cooldown)
    {
        if(!Valid||Main.gamePaused||Entity(key) is not NPC target)return new(false,Message:"目标或世界已变化，未攻击。");
        var snapshot=Snapshot(target);
        if(!CombatRules.Eligible(snapshot)||snapshot.Immune||!snapshot.Visible||CombatRules.Distance(CompanionCombatSystem.Point(agent.Center),snapshot)>CombatRules.Reach)
            return new(false,Message:"目标已离开近身范围或不能攻击。");
        // Keep tML's NPC-versus-NPC modifier/strike/hit-hook sequence, with a final
        // safety check after modifier callbacks, immediately before the live hit.
        if(Entity(key)!=target||!CombatRules.Eligible(Snapshot(target,hooks:false))||!Valid||!CompanionCombatSystem.ReserveStrike(cooldown))return new(false);
        int direction=target.Center.X>=agent.Center.X?1:-1;
        int cap=CombatRules.DamageCap(CompanionCombatSystem.Progress()),capped=Math.Min(damage,cap);
        var modifiers=target.GetIncomingStrikeModifiers(DamageClass.Default,direction);
        NPCLoader.ModifyHitNPC(agent,target,ref modifiers);
        modifiers.SetMaxDamage(cap);modifiers.DisableCrit();
        var hit=modifiers.ToHitInfo(capped,false,2f,damageVariation:false);
        hit.InstantKill=false;hit.Crit=false;hit.Damage=Math.Min(hit.Damage,cap);
        if(Entity(key)!=target||!Valid||!NPCLoader.CanHitNPC(agent,target))return new(false,Message:"命中条件已变化，没有攻击。");
        var last=Snapshot(target,hooks:false);
        var context=new CombatFrame((long)Main.GameUpdateCount,CompanionCombatSystem.Point(player.Center),CompanionCombatSystem.Point(agent.Center),Array.Empty<CombatTarget>(),CompanionCombatSystem.Progress());
        if(last.Key!=key||Entity(key)!=target||!Valid||Main.gamePaused||!CombatRules.Eligible(last)||last.Immune||!last.Visible||CombatRules.Distance(context.Agent,last)>CombatRules.Reach||
            !CombatRules.InScope(_mode,_origin,context,last)||hit.Damage<=0)return new(false,Message:"命中前目标、范围或视线变化，没有攻击。");
        hit.Damage=Math.Min(hit.Damage,CombatRules.DamageCap(CompanionCombatSystem.Progress()));
        var health=target.realLife>=0&&target.realLife<Main.maxNPCs?Main.npc[target.realLife]:target;
        if(!health.active||health.life<=0)return new(false,Message:"目标生命状态已变化。");
        var healthKey=Key(health);int before=health.life;
        agent.direction=direction;agent.spriteDirection=agent.direction;
        Fighting=true;_swingUntil=(long)Main.GameUpdateCount+18;
        target.PlayerInteraction(player.whoAmI);
        target.StrikeNPC(hit,noPlayerInteraction:true);
        NPCLoader.OnHitNPC(agent,target,in hit);
        int actual=Key(health)==healthKey?Math.Clamp(before-Math.Max(0,health.life),0,before):0;
        bool killed=actual>0&&health.life<=0;
        if(actual>0){target.immune[255]=Math.Max(target.immune[255],30);target.netUpdate=true;}
        if(!Main.dedServ)SoundEngine.PlaySound(SoundID.Item1,agent.Center);
        return new(true,actual,killed,actual>0?$"近身命中 {target.TypeName}，实际伤害{actual}。":"已挥击，本次未核实到生命减少。");
    }
    private void BeginFight(){if(_following){_follow.Reset(agent);_following=false;}Fighting=true;}
    public void Hold(){BeginFight();_movement.Reset(CombatRules.Reach-8);_moving=null;MovementHelper.Hold(agent);}
    public void Follow(){Fighting=false;_movement.Reset(CombatRules.Reach-8);_moving=null;if(!_following){_follow.Reset(agent);_following=true;}_follow.Tick(agent,player,4f);}
    public void Release(){if(_moving!=null||Fighting)MovementHelper.Stop(agent);_movement.Reset(CombatRules.Reach-8);_moving=null;Fighting=false;}
    internal void Draw(NPC npc,SpriteBatch batch,Vector2 screen,Color color)
    {
        if(Main.dedServ||!ReferenceEquals(npc,agent)||(!Fighting&&(long)Main.GameUpdateCount>=_swingUntil))return;
        Main.instance.LoadItem(_weapon.type);var texture=TextureAssets.Item[_weapon.type].Value;
        float swing=Math.Clamp((_swingUntil-(long)Main.GameUpdateCount)/18f,0,1);
        float rotation=agent.direction*(swing>0?MathHelper.Lerp(-1.2f,1.3f,1-swing):0.35f);
        float scale=Math.Min(1f,48f/Math.Max(texture.Width,texture.Height));
        batch.Draw(texture,agent.Center-screen+new Vector2(agent.direction*12,2),null,color,rotation,
            new Vector2(agent.direction>0?0:texture.Width,texture.Height),scale,agent.direction>0?SpriteEffects.None:SpriteEffects.FlipHorizontally,0);
    }
}
