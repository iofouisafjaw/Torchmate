using System;
using System.Collections.Generic;
using System.Linq;

namespace TerrarAI.Content.Companion;

public sealed record HealingOptions(bool Enabled=true,int ThresholdPercent=ReminderHealth.LowPercent);
public sealed record HealingFrame(ReminderFrame Context,int PotionDelay,bool PotionSick=false,bool Blocked=false);
public sealed record HealingPotion(int Slot,int Type,int Count,int Heal,bool Usable=true,bool Favorite=false,bool Supported=true);
public sealed record HealingReceipt(bool Invoked,int BeforeLife,int AfterLife,int BeforeCount,int AfterCount,int DelayAfter,bool SickAfter,string Error="")
{
    public int Healed => Math.Max(0,AfterLife-BeforeLife);
    public int Consumed => Math.Max(0,BeforeCount-AfterCount);
}
public sealed record HealingResult(HealingPotion Potion,HealingReceipt Receipt,bool Success,bool Suspended);
public interface IAutomaticHealingSite
{
    HealingFrame Read();
    IReadOnlyList<HealingPotion> Inventory();
    HealingReceipt Use(HealingPotion potion);
}

public static class HealingRules
{
    public const int EmergencyPercent=15;
    public static bool Eligible(HealingFrame f,HealingOptions options)=>options.Enabled&&options.ThresholdPercent is >=10 and <=50
        &&f.Context.Session!=Guid.Empty&&f.Context.Player>=0&&f.Context.Available&&f.Context.Alive&&!f.Context.Paused
        &&f.PotionDelay<=0&&!f.PotionSick&&!f.Blocked&&ReminderHealth.AtMost(f.Context.World.Life,f.Context.World.LifeMax,options.ThresholdPercent);
    public static HealingPotion? Select(int life,int maximum,IEnumerable<HealingPotion> inventory)
    {
        if(life<=0||maximum<=life)return null;
        int missing=maximum-life;
        var choices=inventory.Where(p=>p.Slot is >=0 and <50&&p.Type>0&&p.Count>0&&p.Heal>0&&p.Usable&&!p.Favorite&&p.Supported).ToArray();
        // Do not spend a larger potion merely to cover the last few missing HP.
        var fit=choices.Where(p=>p.Heal<=missing).OrderByDescending(p=>p.Heal).ThenBy(p=>p.Slot).FirstOrDefault();
        if(fit!=null)return fit;
        var over=choices.OrderBy(p=>p.Heal).ThenBy(p=>p.Slot).FirstOrDefault();
        return over!=null&&((long)missing*100>=(long)over.Heal*80||ReminderHealth.AtMost(life,maximum,EmergencyPercent))?over:null;
    }
}

// Production policy, tested without a game world. It never writes life, cooldowns,
// buffs or stacks: the site runs the native quick-heal operation and reports facts.
public sealed class AutomaticHealing
{
    public const int RetryGapTicks=5*60,EmptyScanGapTicks=30;
    private Guid _session;
    private int _player=-1;
    private long _lastFrame=-1,_now,_nextAttempt,_nextScan;
    private bool _suspended,_withoutCooldown;
    public void Reset()
    {_session=Guid.Empty;_player=-1;_lastFrame=-1;_now=_nextAttempt=_nextScan=0;_suspended=_withoutCooldown=false;}
    public HealingResult? Tick(IAutomaticHealingSite site,HealingOptions options)
    {
        var frame=site.Read();var c=frame.Context;
        if(c.Session==Guid.Empty||c.Player<0){Reset();return null;}
        if(c.Session!=_session||c.Player!=_player){Reset();_session=c.Session;_player=c.Player;}
        if(c.Tick<0||c.Tick<=_lastFrame)return null;
        _lastFrame=c.Tick;
        if(c.Paused)return null;
        _now++;
        int recovery=Math.Clamp(options.ThresholdPercent,10,50)+15;
        if(c.Alive&&c.World.LifeMax>0&&(long)c.World.Life*100>=(long)c.World.LifeMax*recovery)
            _suspended=_withoutCooldown=false;
        if(frame.PotionDelay>0||frame.PotionSick)_withoutCooldown=false;
        if(_suspended||_withoutCooldown||_now<_nextAttempt||_now<_nextScan||!HealingRules.Eligible(frame,options))return null;
        IReadOnlyList<HealingPotion> inventory;
        try{inventory=site.Inventory();}
        catch(Exception ex)when(ex is not OutOfMemoryException){_suspended=true;return null;}
        var potion=HealingRules.Select(c.World.Life,c.World.LifeMax,inventory);
        if(potion==null){_nextScan=_now+EmptyScanGapTicks;return null;}
        var live=site.Read();
        if(live.Context.Session!=c.Session||live.Context.Player!=c.Player||live.Context.Tick!=c.Tick
            ||live.Context.World.Life!=c.World.Life||live.Context.World.LifeMax!=c.World.LifeMax||!HealingRules.Eligible(live,options))return null;
        _nextAttempt=_now+RetryGapTicks;
        HealingReceipt receipt;
        try {receipt=site.Use(potion);}
        catch(Exception ex) when(ex is not OutOfMemoryException)
        {receipt=new(true,c.World.Life,c.World.Life,potion.Count,potion.Count,0,false,ex.GetType().Name);}
        bool success=receipt.Invoked&&receipt.Healed>0&&receipt.Error.Length==0;
        // A failing hook must not drain a second potion in this low-health episode.
        if(receipt.Error.Length>0||receipt.Consumed>1||receipt.Invoked&&receipt.Healed==0&&receipt.Consumed>0)_suspended=true;
        if(receipt.Invoked&&(receipt.Healed>0||receipt.Consumed>0)&&receipt.DelayAfter<=0&&!receipt.SickAfter)_withoutCooldown=true;
        return receipt.Invoked||receipt.Error.Length>0?new(potion,receipt,success,_suspended):null;
    }
}

// A narrow CanUseItem scope lets native QuickHeal select only the chosen item.
// No inventory swapping, hiding or rewriting is required. Nested use cannot
// overwrite an active selection, and every exception releases the scope.
public sealed class HealingSelectionGate<TPlayer,TItem> where TPlayer:class where TItem:class
{
    private TPlayer? _player;
    private TItem? _item;
    public bool Allows(TPlayer player,TItem item)=>_player==null||!ReferenceEquals(player,_player)||ReferenceEquals(item,_item);
    public bool Run(TPlayer player,TItem item,Action action)
    {
        if(_player!=null)return false;
        _player=player;_item=item;
        try{action();return true;}finally{_player=null;_item=null;}
    }
}
