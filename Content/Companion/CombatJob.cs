using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace TerrarAI.Content.Companion;

public enum CombatMode { Automatic,Protect,Clear }
public enum CombatState { Running,Completed,Failed,Cancelled }
public readonly record struct EnemyKey(int Slot,int Type,long Generation);
public sealed record CombatTarget(EnemyKey Key,Vector2 Position,float Width=32,float Height=32,int Life=100,int ContactDamage=10,
    bool Active=true,bool Friendly=false,bool Town=false,bool Critter=false,bool Boss=false,bool Immortal=false,
    bool Invulnerable=false,bool Chaseable=true,bool HitAllowed=true,bool Visible=true,bool Immune=false);
public sealed record CombatProgress(bool Eye=false,bool Evil=false,bool Skeletron=false,bool Hardmode=false,
    bool Mech=false,bool Plantera=false,bool Golem=false,bool Moonlord=false);
public sealed record CombatFrame(long Tick,Vector2 Player,Vector2 Agent,IReadOnlyList<CombatTarget> Targets,CombatProgress Progress,
    int WeaponDamage=0,int WeaponUseTicks=48,bool ScopeValid=true,bool PlayerAlive=true,bool Paused=false,bool FollowAllowed=true);
public sealed record CombatHit(bool Attempted,int Damage=0,bool Killed=false,string Message="");
public enum CombatMove { Pending,Reached,Failed }
public interface ICombatSite
{
    CombatTarget? Read(EnemyKey key);
    CombatMove Approach(EnemyKey key,out string message);
    CombatHit Strike(EnemyKey key,int damage,int cooldown);
    void Hold();
    void Follow();
    void Release();
}

public sealed class DefenseGate
{
    public bool Suppressed{get;private set;}
    public void Stop()=>Suppressed=true;
    public void Resume()=>Suppressed=false;
    public bool Allows(bool enabled,bool following,bool idle,bool busy,bool alive)=>enabled&&!Suppressed&&following&&idle&&!busy&&alive;
}

// Kept outside individual jobs so stop/start cannot accelerate attacks.
public sealed class AttackCadence
{
    private long _next;
    public bool Reserve(long tick,int cooldown)
    {if(tick<0||tick<_next)return false;_next=checked(tick+CombatRules.Cooldown(cooldown));return true;}
    public void Reset()=>_next=0;
}

public static class CombatRules
{
    public const float Reach=72,AutomaticRadius=128,ProtectRadius=224,PlayerLeash=288,ClearRadius=384;
    public const int QuietTicks=90,MaxClearTicks=3600,MaxTargetTicks=600;
    public static int DamageCap(CombatProgress p)=>p.Moonlord?60:p.Golem?50:p.Plantera?42:p.Mech?34:p.Hardmode?26:p.Skeletron?18:p.Evil?14:p.Eye?10:8;
    public static int Damage(CombatProgress p,int weapon)=>Math.Clamp(weapon,Math.Max(2,DamageCap(p)*3/4),DamageCap(p));
    public static int Cooldown(int useTicks)=>Math.Clamp(useTicks,48,90);
    public static bool Finite(Vector2 p)=>float.IsFinite(p.X)&&float.IsFinite(p.Y);
    // Hostile is also used by the completion audit: temporarily invulnerable or
    // unchaseable enemies must not silently count as an empty cleared area.
    public static bool Hostile(CombatTarget t)=>t.Active&&t.Life>0&&!t.Friendly&&!t.Town&&!t.Critter&&!t.Boss&&
        Finite(t.Position)&&float.IsFinite(t.Width)&&float.IsFinite(t.Height)&&t.Width>0&&t.Height>0;
    public static bool Eligible(CombatTarget t)=>Hostile(t)&&!t.Immortal&&!t.Invulnerable&&t.Chaseable&&t.HitAllowed;
    public static float Distance(Vector2 point,CombatTarget target)=>Vector2.Max(Vector2.Abs(point-target.Position)-new Vector2(target.Width,target.Height)/2,Vector2.Zero).Length();
    public static bool InScope(CombatMode mode,Vector2 origin,CombatFrame frame,CombatTarget target)
    {
        if(mode==CombatMode.Clear)return Vector2.Distance(target.Position,origin)<=ClearRadius;
        if(Vector2.Distance(frame.Agent,frame.Player)>PlayerLeash)return false;
        if(mode==CombatMode.Protect)return Vector2.Distance(target.Position,frame.Player)<=ProtectRadius;
        return Vector2.Distance(target.Position,frame.Player)<=ProtectRadius&&
            (Vector2.Distance(target.Position,frame.Player)<=AutomaticRadius||Vector2.Distance(target.Position,frame.Agent)<=AutomaticRadius);
    }
}

// Shared production/test controller. Only the native adapter may move or strike;
// every hit must survive a second, fresh identity/hostility/range/visibility check.
public sealed class CombatJob(CombatMode mode,Vector2 origin)
{
    public CombatMode Mode{get;}=mode;
    public Vector2 Origin{get;}=origin;
    public CombatState State{get;private set;}=CombatState.Running;
    public EnemyKey? Target{get;private set;}
    public EnemyKey? LastHit{get;private set;}
    public int Hits{get;private set;}
    public int Kills{get;private set;}
    public int DamageDealt{get;private set;}
    public string Reason{get;private set;}="正在观察附近小怪。";
    public int Remaining{get;private set;}
    private int _elapsed,_quiet,_targetTicks;
    private long _lastTick=long.MinValue,_readyAt;
    private readonly Dictionary<EnemyKey,long> _unreachable=new();
    public bool Tick(CombatFrame frame,ICombatSite site)
    {
        if(State!=CombatState.Running)return false;
        if(!frame.ScopeValid||!frame.PlayerAlive||!CombatRules.Finite(frame.Player)||!CombatRules.Finite(frame.Agent))
        {Cancel(site,"角色、同伴、世界或控制指令已变化，战斗已停止。");return false;}
        if(frame.Paused||frame.Tick<=_lastTick)return Target!=null||Mode!=CombatMode.Automatic;
        _lastTick=frame.Tick;_elapsed++;
        if(Mode==CombatMode.Automatic&&!frame.FollowAllowed){Target=null;site.Release();return false;}
        if(Mode==CombatMode.Clear&&(_elapsed>CombatRules.MaxClearTicks||Vector2.Distance(frame.Agent,Origin)>CombatRules.ClearRadius+64))
        {Fail(site,"清理已到时间或活动范围上限，未保证周围清空；已造成的伤害保留。");return false;}
        if(Mode!=CombatMode.Clear&&Vector2.Distance(frame.Agent,frame.Player)>CombatRules.PlayerLeash)
        {
            Target=null;_targetTicks=0;site.Release();Reason="回到玩家身边。";
            if(Mode==CombatMode.Automatic)return false;
            site.Follow();return true;
        }
        foreach(var key in _unreachable.Where(p=>p.Value<=frame.Tick).Select(p=>p.Key).ToArray())_unreachable.Remove(key);
        var remaining=frame.Targets.Where(t=>CombatRules.Hostile(t)&&CombatRules.InScope(Mode,Origin,frame,t)).ToArray();
        Remaining=remaining.Length;
        var candidates=remaining.Where(t=>CombatRules.Eligible(t)&&!_unreachable.ContainsKey(t.Key)).ToArray();
        var selected=candidates.FirstOrDefault(t=>t.Key==Target);
        var closest=candidates.OrderBy(t=>Mode==CombatMode.Clear?Vector2.DistanceSquared(t.Position,frame.Agent):Vector2.DistanceSquared(t.Position,frame.Player))
            .ThenBy(t=>Vector2.DistanceSquared(t.Position,frame.Agent)).ThenBy(t=>t.Key.Slot).FirstOrDefault();
        if(selected==null||Mode!=CombatMode.Clear&&closest!=null&&Vector2.Distance(closest.Position,frame.Player)<80&&Vector2.Distance(selected.Position,frame.Player)>128)selected=closest;
        if(selected==null)
        {
            Target=null;_targetTicks=0;site.Release();
            if(Mode==CombatMode.Clear)
            {
                if(Remaining>0){Fail(site,$"范围内还有{Remaining}个无法接近或攻击的小怪，清理未完成。");return false;}
                site.Hold();Reason=$"正在核查清理范围 · {_quiet}/{CombatRules.QuietTicks}";
                if(++_quiet>=CombatRules.QuietTicks){State=CombatState.Completed;Reason=$"已核查起点附近24格内当前无可处理小怪。同伴实际击杀{Kills}个，命中{Hits}次。";}
                return true;
            }
            Reason=Remaining>0?$"附近还有{Remaining}个暂时无法攻击的小怪，留在玩家身边等待机会。":
                Mode==CombatMode.Protect?"保护中，附近暂时安全。":"跟随中，留意靠近的小怪。";
            if(Mode==CombatMode.Protect){site.Follow();return true;}return false;
        }
        _quiet=0;
        if(Target!=selected.Key){site.Release();Target=selected.Key;_targetTicks=0;}
        if(++_targetTicks>CombatRules.MaxTargetTicks)
        {_unreachable[selected.Key]=Mode==CombatMode.Clear?long.MaxValue:frame.Tick+300;Target=null;site.Release();Reason="这个目标久未解决，改看其他小怪。";return true;}
        if(CombatRules.Distance(frame.Agent,selected)>CombatRules.Reach||!selected.Visible)
        {
            var moved=site.Approach(selected.Key,out string message);Reason=message;
            if(moved==CombatMove.Failed){_unreachable[selected.Key]=Mode==CombatMode.Clear?long.MaxValue:frame.Tick+300;Target=null;site.Release();}
            return true;
        }
        site.Hold();Reason="近身护卫，等待出手。";
        if(frame.Tick<_readyAt||selected.Immune)return true;
        var live=site.Read(selected.Key);
        if(live==null||live.Key!=selected.Key||!CombatRules.Eligible(live)||live.Immune||!live.Visible||
            !CombatRules.InScope(Mode,Origin,frame,live)||CombatRules.Distance(frame.Agent,live)>CombatRules.Reach)
        {Target=null;site.Release();Reason="目标状态已变化，未攻击。";return true;}
        int cooldown=CombatRules.Cooldown(frame.WeaponUseTicks);
        var hit=site.Strike(live.Key,CombatRules.Damage(frame.Progress,frame.WeaponDamage),cooldown);
        if(hit.Attempted)_readyAt=frame.Tick+cooldown;
        if(hit.Damage>0){LastHit=live.Key;Hits++;DamageDealt+=hit.Damage;_targetTicks=0;if(hit.Killed){Kills++;Target=null;}}
        Reason=hit.Message.Length>0?hit.Message:hit.Damage>0?$"实际造成{hit.Damage}点伤害。":"这次未造成伤害。";
        return true;
    }
    public void Cancel(ICombatSite? site,string reason)
    {if(State!=CombatState.Running)return;site?.Release();Target=null;State=CombatState.Cancelled;Reason=reason;}
    private void Fail(ICombatSite site,string reason){site.Release();Target=null;State=CombatState.Failed;Reason=reason;}
}
