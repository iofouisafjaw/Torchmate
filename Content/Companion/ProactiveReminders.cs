using System;
using System.Collections.Generic;

namespace TerrarAI.Content.Companion;

public enum ReminderKind { LowHealth,BloodMoon,Night,Inventory }
public sealed record ReminderOptions(bool Enabled=true,bool Night=true,bool BloodMoon=true,bool LowHealth=true,bool Inventory=true);
public sealed record ReminderWorldState(bool Day,bool BloodMoon,int Life,int LifeMax,int FreeSlots);
public sealed record ReminderFrame(Guid Session,int Player,long Tick,ReminderWorldState World,bool Available=true,bool Alive=true,bool Paused=false,bool HealthHandled=false);
public sealed record ReminderNotice(ReminderKind Kind,string Text);
public static class ReminderHealth
{
    public const int LowPercent=30,RecoveryPercent=45;
    public static bool AtMost(int life,int maximum,int percent=LowPercent)=>life>0&&maximum>0&&percent is >0 and <=100
        &&(long)life*100<=(long)maximum*percent;
}
public static class ReminderInventory
{
    public static int FreeSlots<T>(IReadOnlyList<T>? inventory,Func<T,bool> empty)
    {
        if(inventory==null||inventory.Count<50)return -1;
        int free=0;for(int i=0;i<50;i++)if(empty(inventory[i]))free++;return free;
    }
}

// Pure policy shared by the live adapter and tests. Cooldowns use observed active
// world updates, not wall-clock time, accelerated Terraria time or model output.
public sealed class ProactiveReminders
{
    public const int GlobalGapTicks=60*60,KindGapTicks=5*60*60,WarmupTicks=5*60,NightLifetimeTicks=2*60*60;
    private readonly Dictionary<ReminderKind,long> _sent=new();
    private Guid _session;
    private int _player=-1,_presentTicks;
    private long _lastFrame=-1,_now,_nightAt;
    private long? _lastSent;
    private bool _initialized,_day,_blood,_bloodSent,_nightPending,_low,_lowSent,_full,_fullSent;

    public void Reset()
    {
        _session=Guid.Empty;_player=-1;_lastFrame=-1;_now=_nightAt=0;_lastSent=null;
        _presentTicks=0;_initialized=_day=_blood=_bloodSent=_nightPending=_low=_lowSent=_full=_fullSent=false;_sent.Clear();
    }
    private bool Due(ReminderKind kind)=>!_sent.TryGetValue(kind,out long at)||_now-at>=KindGapTicks;
    public ReminderNotice? Tick(ReminderFrame frame,ReminderOptions options)
    {
        if(frame.Session==Guid.Empty){Reset();return null;}
        if(frame.Session!=_session||frame.Player!=_player)
        {Reset();_session=frame.Session;_player=frame.Player;}
        if(frame.Tick<0||frame.Tick<=_lastFrame)return null;
        _lastFrame=frame.Tick;
        if(frame.Paused)return null;
        _now++;
        var world=frame.World;
        bool blood=world.BloodMoon&&!world.Day;
        bool available=options.Enabled&&frame.Available&&frame.Alive&&world.Life>0;
        if(_initialized&&_day&&!world.Day&&available&&options.Night)
        {_nightPending=true;_nightAt=_now;}
        if(world.Day||!available||!options.Night||_now-_nightAt>NightLifetimeTicks)_nightPending=false;
        if(blood&&options.BloodMoon)_nightPending=false; // Blood moon replaces the ordinary dusk notice.
        if(!blood||!_blood)_bloodSent=false;
        _day=world.Day;_blood=blood;_initialized=true;

        bool healthValid=frame.Alive&&world.Life>0&&world.LifeMax>0;
        if(!healthValid||(long)world.Life*100>=(long)world.LifeMax*45){_low=false;_lowSent=false;}
        else if(!_low&&ReminderHealth.AtMost(world.Life,world.LifeMax)){_low=true;_lowSent=false;}
        bool bagValid=world.FreeSlots is >=0 and <=50;
        if(!bagValid||world.FreeSlots>=8){_full=false;_fullSent=false;}
        else if(!_full&&world.FreeSlots<=3){_full=true;_fullSent=false;}

        if(!available){_presentTicks=0;return null;}
        _presentTicks=Math.Min(_presentTicks+1,WarmupTicks);
        // A native healing receipt is already visible this frame. Count it toward
        // shared UI spacing and this still-low episode without repeating a warning.
        if(frame.HealthHandled){if(_low)_lowSent=true;_lastSent=_now;return null;}
        if(_presentTicks<WarmupTicks)return null;
        if(_lastSent is long last&&_now-last<GlobalGapTicks)return null;
        ReminderNotice? notice=null;
        // Hysteresis preserves the episode latch, but pending notices must also
        // match today's actual trigger threshold: no stale low-health/full-bag text.
        if(options.LowHealth&&_low&&!_lowSent&&healthValid&&ReminderHealth.AtMost(world.Life,world.LifeMax)&&Due(ReminderKind.LowHealth))
        {notice=new(ReminderKind.LowHealth,$"生命只剩{world.Life}/{world.LifeMax}了，先留意安全，必要时手动治疗。");_lowSent=true;}
        else if(options.BloodMoon&&blood&&!_bloodSent&&Due(ReminderKind.BloodMoon))
        {notice=new(ReminderKind.BloodMoon,"现在是血月，留意周围的敌怪。");_bloodSent=true;_nightPending=false;}
        else if(options.Night&&_nightPending&&!world.Day&&Due(ReminderKind.Night))
        {notice=new(ReminderKind.Night,"天黑了，出门留意周围。");_nightPending=false;}
        else if(options.Inventory&&_full&&!_fullSent&&bagValid&&world.FreeSlots<=3&&Due(ReminderKind.Inventory))
        {notice=new(ReminderKind.Inventory,world.FreeSlots==0?"主背包没有空位了，可以找个安全地方整理一下。":$"主背包只剩{world.FreeSlots}个空位了，可以考虑整理一下。");_fullSent=true;}
        if(notice==null)return null;
        _lastSent=_now;_sent[notice.Kind]=_now;return notice;
    }
}
