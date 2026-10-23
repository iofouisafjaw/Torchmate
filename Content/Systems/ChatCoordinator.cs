using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using TerrarAI.Content.Companion;
using TerrarAI.Content.NPCs;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrarAI.Content.Systems;

public sealed class ChatCoordinator : ModSystem
{
    // Current single-companion lifecycle; command routing below is the local
    // companion integration added after the inherited version.
    private bool _enteredWorld;
    public override void OnWorldLoad() { _enteredWorld = false; }
    public override void OnWorldUnload() { _enteredWorld = false; }
    public override void PostUpdateWorld()
    {
        if (_enteredWorld || Main.netMode != NetmodeID.SinglePlayer) return;
        foreach (var player in Main.player)
            if (player is { active: true, dead: false })
            { _enteredWorld = EnsureCompanion(player) != null; break; }
    }

    internal static AIAgentNPC? FindCompanion()
    {
        foreach (var entity in Main.npc)
            if (entity is { active: true, ModNPC: AIAgentNPC companion }) return companion;
        return null;
    }

    private static AIAgentNPC? EnsureCompanion(Player player)
    {
        if (FindCompanion() is { } present) return present;
        if (Main.netMode != NetmodeID.SinglePlayer || !player.active || player.dead) return null;
        int slot = NPC.NewNPC(new EntitySource_Misc("TerrarAI.Companion"),
            (int)player.Center.X + 48, (int)player.Bottom.Y, ModContent.NPCType<AIAgentNPC>());
        if (slot < 0 || slot >= Main.npc.Length || Main.npc[slot].ModNPC is not AIAgentNPC created) return null;
        created.NPC.GivenName = "Qwen"; created.NPC.netUpdate = true;
        return created;
    }

    internal static AIAgentNPC? GetOrCreateCompanion(Player player) => EnsureCompanion(player);

    internal static void HandleInput(CommandCaller caller, string text)
        => HandleInput(caller.Player, text, (message, color) =>
        {
            CompanionUISystem.Publish(message, color);
            caller.Reply(message, color);
        });

    internal static void HandleInput(Player player, string text, Action<string, Color> reply)
    {
        void Reply(string message, Color color = default) => reply(message, color == default ? Color.White : color);
        if (Main.netMode != NetmodeID.SinglePlayer)
        { Reply("This companion prototype currently supports single player.", Color.OrangeRed); return; }
        using var recoveryInput=CompanionRecoverySystem.Input(player,text);
        if (ObservationPolicy.Matches(text))
        {
            try
            {
                var view=CompanionTaskTracker.Current;
                string result=ObservationPolicy.Describe(view);
                if(view!=null&&CompanionExpeditionSystem.IsTask(view.TaskId))result+="\n"+CompanionExpeditionSystem.Describe();
                // Capture the real receipt before cancellation updates the task card.
                if(ObservationPolicy.StopsWork(text))CompanionCommandService.Execute(player,"wait");
                var bag=NearbyChests.Stock(player,false);var total=NearbyChests.Stock(player);
                string stock=string.Join("、",total.Keys.Union(bag.Keys).OrderBy(i=>i).Select(i=>
                    $"{Crafting.Name(i)}（背包{bag.GetValueOrDefault(i)}／箱子{Math.Max(0,total.GetValueOrDefault(i)-bag.GetValueOrDefault(i))}）"));
                Reply(result+"\n当前资源："+(stock.Length==0?"空":stock)+"\n"+ObservationPolicy.Blocked,Color.LightBlue);
            }
            catch(Exception ex)
            {
                if(ObservationPolicy.StopsWork(text))CompanionCommandService.Execute(player,"wait");
                Reply("本次状态检查未完成："+ex.Message+"\n"+ObservationPolicy.Blocked,Color.Orange);
            }
            return;
        }
        if (CompanionCollaborationSystem.TryInput(player, text, out string collaborationReply))
        { Reply(collaborationReply, Color.LightBlue); return; }
        if(CompanionRecoverySystem.TryStop(text)){Reply("已停止恢复，已完成部分保留。",Color.LightBlue);return;}
        string[] parts = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string operation = parts.Length == 0 ? "help" : parts[0].ToLowerInvariant();
        string argument = parts.Length > 1 ? parts[1].Trim() : "";
        if (operation is not ("ask" or "world" or "status" or "memory" or "help")) CompanionInquirySystem.Cancel();
        try
        {
            if (CompanionCommandService.TryParse(text, out string direct))
            { Reply(CompanionCommandService.Execute(player, direct), Color.LightBlue); return; }
            if (CheatWorldRequest.TryParse(text,out var worldCheat,out string cheatError))
            {
                if(worldCheat==null) Reply(cheatError,Color.Orange);
                else
                {
                    // Direct, deterministic command; no model needed and no repeated agent tool step.
                    CompanionAgent.Cancel(); CompanionDialogueSystem.Cancel();
                    Reply(CheatTools.World(player,worldCheat),Color.LightBlue);
                }
                return;
            }
            if (GardenRequest.TryParse(text,out var garden,out string gardenError))
            { Reply(garden==null?gardenError:CompanionGardenSystem.Begin(player,garden),Color.LightBlue); CompanionUISystem.OpenChat(); return; }
            if (CombatRequest.TryParse(text, out string combatMode))
            { Reply(CompanionCombatSystem.Begin(player, combatMode), Color.LightBlue); return; }
            if (LocationRequest.TryParse(text, out var location, MapLocationSystem.KnownOre))
            { Reply(MapLocationSystem.Query(player, location!), Color.LightBlue); return; }
            if (OreTunnelRequest.TryParse(text,out var tunnel))
            { Reply(CompanionExpeditionSystem.BeginTunnel(player,tunnel!),Color.LightBlue); return; }
            if (ExpeditionRequest.TryParse(text, out var expedition, out string expeditionError))
            { Reply(expedition==null?expeditionError:CompanionExpeditionSystem.Begin(player, expedition), Color.LightBlue); return; }
            // Plain-Chinese ways into what used to be panel pages.
            string plain = text.Trim().TrimEnd('？', '?', '。', '！', '!');
            if (ChestRequest.TryParse(text, out var chestRequest, out string chestError))
            {
                if (chestRequest == null) { Reply(chestError, Color.Orange); return; }
                if (chestRequest.Operation != ChestOperation.Query)
                {
                    CompanionCommandService.Advance(); CompanionAgent.Cancel(); CompanionDialogueSystem.Cancel();
                    FindCompanion()?.StopCommand("chest_transfer_requested");
                }
                Reply(NearbyChests.Transfer(player, chestRequest), Color.LightBlue); return;
            }
            if (ArenaRequest.Matches(text))
            { CompanionUISystem.StartArena(); return; }
            if (HousingRequest.Matches(text))
            { CompanionUISystem.StartHousing(); return; }
            if (PlatformRequest.TryParse(text, out var platform, out string platformError))
            {
                if (platform == null) Reply(platformError, Color.Orange);
                else CompanionUISystem.StartConstruction(platform);
                return;
            }
            if (CraftChainRequest.TryParse(text, out var chain, out string chainError) &&
                (chain == null || chain.IronSet || Crafting.ResolveExact(chain.Item) > 0 || System.Text.RegularExpressions.Regex.IsMatch(text,"规划|计划|完整合成|全流程")))
            {
                Reply(chain == null ? chainError : CraftChainSystem.Begin(player, chain), Color.LightBlue);
                CompanionUISystem.OpenChat(); return;
            }
            var forget = System.Text.RegularExpressions.Regex.Match(plain, @"^(?:忘记|忘掉|删掉|删除)(?:第)?\s*(\d+)\s*条?$");
            if (forget.Success) { operation = "forget"; argument = forget.Groups[1].Value; }
            else if (plain is "记忆" or "偏好" or "你记得什么" or "你记住了什么" or "你记得我什么" or "我的偏好")
                operation = "memory";
            else if (plain is "世界近况" or "世界信息" or "世界状态")
                operation = "world";
            else if (plain is "检查连接" or "测试连接" or "连接测试")
            { CompanionUISystem.CheckConnection(); return; }
            else if (System.Text.RegularExpressions.Regex.IsMatch(plain, @"(搭|铺|建|修|造|放)(一些|几层|几排|点)?(木)?平台|平台工坊"))
            { CompanionUISystem.StartConstruction(); return; }
            switch (operation)
            {
                case "help":
                    Reply("/ai <聊天或任务> | 作弊开关后：时间改成21:00／天气改成晴天／召唤克眼／把世界出生点设在这里 | 种3颗橡实 | 种5株太阳花 | 采收草药 | 帮我准备打克眼 | 挖到地下层 | 往下挖一条通道 | 去前面探探 | 最近的铁矿在哪 | 附近箱子在哪 | 我的床在哪 | 查看标记 | 清除标记 | 保护我 | 清理附近小怪 | 停止攻击 | wait/等待 | follow/继续跟随 | stop | recall | spawn | dismiss | ask <问题> | world | confirm/同意 | reject/拒绝 | memory | forget <编号> | status", Color.LightBlue);
                    return;
                case "ask":
                    CompanionInquirySystem.Submit(player, argument);
                    Reply("正在查看提问时的世界摘要……", Color.LightBlue);
                    return;
                case "world":
                    var world = CompanionWorldInfo.Capture(player);
                    Reply($"{world.Name} · {world.Size}{world.Difficulty} · {world.Stage}\n{world.Clock} · {world.Weather} · {world.Biome}\n生命 {world.Life}/{world.LifeMax} · 附近敌怪 {world.NearbyHostiles}", Color.LightBlue);
                    Reply("原版首领进度：" + string.Join("、", world.Defeated.DefaultIfEmpty("尚未击败已列出的首领")));
                    return;
                case "memory":
                    var memory = RequireStore();
                    Reply("这个世界里我记住的：", Color.LightBlue);
                    int offset = memory.Memory.Preferences.Count;
                    for (int i = 0; i < offset; i++)
                    {
                        var preference = memory.Memory.Preferences[i];
                        Reply($"{i + 1}. {PreferenceProtocol.Describe(preference)}", Color.LightGray);
                    }
                    for (int i = 0; i < memory.Memory.Notes.Count; i++)
                        Reply($"{offset + i + 1}. {memory.Memory.Notes[i]}", Color.LightGray);
                    if (offset + memory.Memory.Notes.Count == 0) Reply("还没记住什么。可以跟我说：以后采集前先问我。");
                    else Reply("想删哪条就说：忘掉 1", Color.Gray);
                    foreach (var recent in memory.Memory.RecentEvents.TakeLast(2)) Reply("最近：" + recent, Color.Gray);
                    return;
                case "remember":
                    CompanionDialogueSystem.Cancel();
                    RequireStore().Remember(argument);
                    CompanionMemorySystem.Record("preference_added", null, null, new { text = argument, player = player.name });
                    Reply("记住了：" + argument, Color.LightGreen);
                    return;
                case "forget":
                    if (!int.TryParse(argument, out int index)) throw new ArgumentException("说「记忆」看编号，再说「忘掉 1」。");
                    CompanionDialogueSystem.Cancel();
                    var store = RequireStore();
                    if (index <= store.Memory.Preferences.Count) store.ForgetPreference(index - 1);
                    else store.Forget(index - store.Memory.Preferences.Count - 1);
                    CompanionMemorySystem.Record("preference_removed", null, null, new { index });
                    Reply("好，那条忘掉了。", Color.LightGreen);
                    return;
                case "feedback":
                    if (argument.Length is < 1 or > 120) throw new ArgumentException("Feedback must contain 1–120 characters.");
                    RequireStore();
                    CompanionDialogueSystem.Submit(player, argument, feedbackOnly: true);
                    Reply("反馈已记录，正在理解其中的偏好。", Color.LightGreen);
                    return;
                case "status":
                    Reply("Local model: " + TerrarAI_Config.Get().Model + " at " + TerrarAI_Config.Get().BaseEndpoint, Color.LightBlue);
                    Reply("Memory folder: " + RequireStore().DirectoryPath);
                    Reply("Companion: " + (FindCompanion() == null ? "absent" : "present"));
                    Reply("Rule revision: " + RequireStore().Memory.RuleRevision + "; awaiting clarification: " + CompanionDialogueSystem.AwaitingClarification);
                    return;
            }
            var companion = EnsureCompanion(player) ?? throw new InvalidOperationException("Could not spawn companion.");
            switch (operation)
            {
                case "confirm":
                    Reply(companion.ConfirmPendingAction(player)); return;
                case "reject":
                case "拒绝":
                case "不同意":
                    CompanionCommandService.Advance(); CompanionDialogueSystem.Cancel(); companion.StopCommand("player_rejected"); Reply("已取消。 "); return;
            }
            if (companion.HasPendingApproval && text.Trim().TrimEnd('。', '！', '!', '.', '，', ',') is
                "同意" or "确认" or "可以" or "好" or "好的" or "可以开始" or "这次不用问了" or "这次可以" or "这次直接做吧")
            { Reply(companion.ConfirmPendingAction(player)); return; }
            if (text.Length > CompanionProtocol.MaxCommandLength) throw new ArgumentException("Instruction is too long (maximum 240 characters).");
            CompanionDialogueSystem.Submit(player, text);
            Reply("Qwen 正在听你说……", Color.LightBlue);
        }
        catch (Exception ex) { Reply(ex.Message, Color.OrangeRed); }
    }

    private static CompanionStore RequireStore() => CompanionMemorySystem.Store
        ?? throw new InvalidOperationException("World memory is unavailable. Check client.log.");
    internal static void RecallAllAgents(Player player, CommandCaller caller) => caller.Reply(CompanionCommandService.Execute(player, "recall"));
    internal static void RouteTool(CommandCaller caller, string text) => HandleInput(caller, text);
}

internal sealed class AICommand : ModCommand
{
    public override CommandType Type => CommandType.Chat;
    public override string Command => "ai";
    public override string Usage => "/ai <instruction or help>";
    public override string Description => "Control your local Qwen companion and this world's memory.";
    public override void Action(CommandCaller caller, string input, string[] args) =>
        ChatCoordinator.HandleInput(caller, string.Join(" ", args));
}
