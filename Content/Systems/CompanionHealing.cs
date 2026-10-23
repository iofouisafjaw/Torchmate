using System;
using System.Collections.Generic;
using System.Linq;
using TerrarAI.Content.Companion;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrarAI.Content.Systems;

internal sealed class NativeHealingSite(Player player):IAutomaticHealingSite
{
    private readonly Guid _session=CompanionMemorySystem.Session;
    private readonly Dictionary<int,Item> _items=new();
    internal static HealingOptions Options()=>new(TerrarAI_Config.Get().EnableAutomaticHealing,TerrarAI_Config.Get().AutoHealingThresholdPercent);
    private static bool Supported(Item item)=>item.type is ItemID.LesserHealingPotion or ItemID.HealingPotion
        or ItemID.GreaterHealingPotion or ItemID.SuperHealingPotion or ItemID.RestorationPotion
        &&item.ModItem==null&&item.potion&&item.consumable&&item.healLife>0;
    public HealingFrame Read()
    {
        bool available=!Main.dedServ&&!Main.gameMenu&&Main.netMode==NetmodeID.SinglePlayer&&ReferenceEquals(Main.LocalPlayer,player)
            &&player.active&&_session==CompanionMemorySystem.Session&&ChatCoordinator.FindCompanion()?.NPC.active==true;
        var context=new ReminderFrame(CompanionMemorySystem.Session,player.whoAmI,(long)Main.GameUpdateCount,CompanionWorldInfo.ForReminders(player),
            available,!player.dead&&!player.ghost,Main.gamePaused||ModContent.GetInstance<CompanionUISystem>().WantsPause);
        return new(context,player.potionDelay,player.HasBuff(BuffID.PotionSickness),player.noItems||player.cursed||player.CCed);
    }
    private HealingPotion Describe(int slot,Item item)=>new(slot,item.type,item.stack,player.GetHealLife(item,true),
        CombinedHooks.CanUseItem(player,item),item.favorited,Supported(item));
    public IReadOnlyList<HealingPotion> Inventory()
    {
        _items.Clear();var result=new List<HealingPotion>();
        for(int slot=0;slot<Math.Min(50,player.inventory.Length);slot++)
        {
            var item=player.inventory[slot];
            if(item==null||item.IsAir||item.favorited||!Supported(item))continue;
            _items[slot]=item;result.Add(Describe(slot,item));
        }
        return result;
    }
    private int Count(int type)=>player.inventory.Take(50).Where(i=>i.type==type).Sum(i=>i.stack);
    public HealingReceipt Use(HealingPotion potion)
    {
        HealingReceipt Rejected()=>new(false,player.statLife,player.statLife,Count(potion.Type),Count(potion.Type),player.potionDelay,player.HasBuff(BuffID.PotionSickness));
        if(!HealingRules.Eligible(Read(),Options())||!_items.TryGetValue(potion.Slot,out var original)
            ||potion.Slot>=player.inventory.Length||!ReferenceEquals(player.inventory[potion.Slot],original)||Describe(potion.Slot,original)!=potion)return Rejected();
        // Recheck after mod CanUseItem/GetHealLife hooks; no queued or stale use.
        var live=Read();
        if(!HealingRules.Eligible(live,Options())||HealingRules.Select(player.statLife,player.statLifeMax2,new[]{potion})==null)return Rejected();
        int beforeLife=player.statLife,beforeCount=Count(potion.Type);bool invoked=false;string error="";
        try
        {
            CompanionHealingItemGate.Selection.Run(player,original,()=>{invoked=true;player.QuickHeal();});
        }
        catch(Exception ex)when(ex is not OutOfMemoryException){error=ex.GetType().Name;}
        return new(invoked,beforeLife,player.statLife,beforeCount,Count(potion.Type),player.potionDelay,player.HasBuff(BuffID.PotionSickness),error);
    }
}

// Native QuickHeal still owns item usability, sounds, healing hooks, buffs,
// PotionDelayModifier and consumption. This temporary filter changes selection
// only for this player and only during that one synchronous QuickHeal call.
public sealed class CompanionHealingItemGate:GlobalItem
{
    internal static readonly HealingSelectionGate<Player,Item> Selection=new();
    public override bool CanUseItem(Item item,Player player)=>!item.potion||item.healLife<=0||Selection.Allows(player,item);
}
