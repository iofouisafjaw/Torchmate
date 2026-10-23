using System;
using TerrarAI.Content.Companion;
using Terraria;
using Terraria.ID;

namespace TerrarAI.Content.Systems;

internal static class CompanionCommandService
{
    internal static long Revision { get; private set; }
    internal static ControlStamp Capture() => new(CompanionMemorySystem.Session, Revision);
    internal static void Advance()
    {
        CompanionCollaborationSystem.ExternalCommand();
        CompanionRecoverySystem.Invalidate();
        Revision++;
        CompanionGardenSystem.Cancel("收到新指令，种植／采收已停止，已完成部分保留。");
        CompanionExpeditionSystem.Cancel("收到新指令，挖掘或探路任务已停止。");
        CompanionCombatSystem.Cancel("收到新指令，护卫或清理任务已停止。");
        CraftChainSystem.Cancel("收到新的控制指令，合成链已取消，已完成部分保留。");
        CompanionConstructionSystem.Cancel("收到新的控制指令，施工已取消。");
        StructureWorkshop.Cancel("收到新的控制指令，施工已取消。");
    }

    internal static bool TryParse(string text, out string operation)
    {
        operation = text.Trim().TrimEnd('。', '！', '!', '.', '，', ',').ToLowerInvariant() switch {
            "wait" or "等待" or "原地等待" or "在这里等" or "在这里等我" => "wait",
            "follow" or "跟随" or "继续跟随" or "跟着我" => "follow",
            "stop" or "停止" => "stop",
            "recall" or "回来" or "come back" or "come here" or "召回" => "recall",
            "dismiss" or "暂别" => "dismiss", "spawn" or "唤来同伴" => "spawn", _ => "" };
        return operation.Length > 0;
    }

    internal static string Execute(Player player, string operation, bool fromAgent = false)
    {
        if (Main.netMode != NetmodeID.SinglePlayer) return "基础同伴控制目前仅支持单人游戏。";
        if (!player.active || player.dead) return "请在角色存活时控制同伴。";
        if (operation == "spawn")
            return ChatCoordinator.GetOrCreateCompanion(player) == null ? "同伴未能出现，请重试。" : "同伴已经在场，每个世界同时只有一位 Qwen。";
        var agent = ChatCoordinator.FindCompanion();
        // Always invalidate before releasing requests. Completion can arrive despite HTTP cancellation.
        Advance();
        CompanionDialogueSystem.Cancel();
        CompanionInquirySystem.Cancel();
        if (!fromAgent) CompanionAgent.Cancel();   // a direct order from the player ends the agent's goal
        if (agent == null) return "同伴暂时不在场，请先到同伴页唤来。";
        switch (operation)
        {
            case "wait": agent.StopCommand("player_waiting"); agent.WaitHere(player); return "我会留在这里。点击继续跟随或召回就能叫我回来。";
            case "follow":
            {
                bool hadTask = agent.CurrentCommandForDialogue != null;
                agent.StopCommand("player_follow"); agent.ResumeFollowing(player);
                return hadTask ? "已取消旧任务，继续跟随你。" : "好，跟着你。";
            }
            case "stop": agent.StopCommand("player_stopped"); agent.ResumeFollowing(player); CompanionCombatSystem.Suppress(); return "已停止任务和攻击，同伴恢复跟随；说继续跟随可恢复自动反击。";
            case "recall": agent.RecallToCommander(player); agent.ResumeFollowing(player); return "同伴已召回，恢复跟随。";
            case "dismiss": agent.StopCommand("dismissed"); agent.NPC.active = false; return "同伴暂时离开，世界记忆保留。";
            default: throw new ArgumentException("未知的基础同伴操作。");
        }
    }
}
