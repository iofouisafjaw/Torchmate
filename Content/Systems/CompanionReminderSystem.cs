using Microsoft.Xna.Framework;
using TerrarAI.Content.Companion;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrarAI.Content.Systems;

// Shared health observation for optional native quick-heal and passive reminders.
// Neither path calls models, routes commands or changes the active task.
public sealed class CompanionReminderSystem:ModSystem
{
    private readonly ProactiveReminders _reminders=new();
    private readonly AutomaticHealing _healing=new();
    public override void PostUpdateWorld()
    {
        if(Main.dedServ||Main.gameMenu||Main.netMode!=NetmodeID.SinglePlayer)return;
        var player=Main.LocalPlayer;if(!player.active)return;
        var config=TerrarAI_Config.Get();
        var treatment=_healing.Tick(new NativeHealingSite(player),NativeHealingSite.Options());
        if(treatment!=null)
        {
            var r=treatment.Receipt;
            if(treatment.Success||treatment.Suspended)
            {
                string text=treatment.Success?$"自动使用{Lang.GetItemNameValue(treatment.Potion.Type)}，实际恢复{r.Healed}点生命，消耗{r.Consumed}瓶；当前{player.statLife}/{player.statLifeMax2}。":
                    $"自动治疗未正常生效（恢复{r.Healed}、消耗{r.Consumed}瓶），本轮已暂停自动用药，请检查状态。";
                if(treatment.Success&&treatment.Suspended)text+=" 本次消耗异常，本轮已暂停自动用药。";
                CompanionUISystem.Notify("Qwen："+text,treatment.Success&&!treatment.Suspended?Color.LightGreen:Color.Orange);
            }
            CompanionMemorySystem.Record("companion_healing",null,null,new{treatment.Potion.Type,treatment.Potion.Slot,r.Invoked,r.Healed,r.Consumed,r.BeforeLife,r.AfterLife,
                r.DelayAfter,r.SickAfter,r.Error,treatment.Success,treatment.Suspended});
        }
        var options=new ReminderOptions(config.EnableReminders,config.EnableNightReminders,config.EnableBloodMoonReminders,config.EnableLowHealthReminders,config.EnableInventoryReminders);
        var world=CompanionWorldInfo.ForReminders(player);
        var frame=new ReminderFrame(CompanionMemorySystem.Session,player.whoAmI,(long)Main.GameUpdateCount,world,
            ChatCoordinator.FindCompanion()?.NPC.active==true,!player.dead,Main.gamePaused,
            treatment?.Success==true||treatment?.Suspended==true);
        var notice=_reminders.Tick(frame,options);
        if(notice==null)return;
        CompanionUISystem.Notify("Qwen："+notice.Text,notice.Kind==ReminderKind.LowHealth?Color.OrangeRed:Color.LightGoldenrodYellow);
        CompanionMemorySystem.Record("companion_reminder",null,null,new{kind=notice.Kind.ToString(),notice.Text,world});
    }
    private void Reset(){_reminders.Reset();_healing.Reset();}
    public override void OnWorldLoad()=>Reset();
    public override void OnWorldUnload()=>Reset();
    public override void ClearWorld()=>Reset();
    public override void Unload()=>Reset();
}
