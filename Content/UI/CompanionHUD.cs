using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Graphics;
using TerrarAI.Content.Companion;
using TerrarAI.Content.Systems;
using Terraria;
using Terraria.GameContent;

namespace TerrarAI.Content.UI;

internal static class CompanionDisplay
{
    internal static string StateText()
    {
        if (CompanionGardenSystem.Active) return Main.gamePaused?"种植／采收 · 已暂停":CompanionGardenSystem.Status;
        if (CompanionExpeditionSystem.Active) return Main.gamePaused ? "探路／挖掘 · 已暂停" : CompanionExpeditionSystem.Status;
        if (CompanionCombatSystem.Active) return Main.gamePaused ? "护卫 · 已暂停" : CompanionCombatSystem.Status;
        if (CompanionCombatSystem.Fighting) return "跟随 · 自动反击";
        if (CraftChainSystem.Active) return CraftChainSystem.Asking ? "合成链 · 等你确认" : CraftChainSystem.Job?.State == ChainState.Preview ? "合成链 · 请补齐条件" : Main.gamePaused ? "合成链 · 已暂停" : "合成链 · 执行中";
        if (CompanionConstructionSystem.Active) return CompanionConstructionSystem.Phase switch {
            ConstructionPhase.Selecting => "平台工坊 · 正在框选", ConstructionPhase.Preview => "平台工坊 · 等你确认",
            _ when CompanionConstructionSystem.SupplyAsking => "平台备料 · 等你同意砍树",
            _ when CompanionConstructionSystem.Supply?.Stage == SupplyStage.Gathering => "平台工坊 · 正在备料",
            _ => Main.gamePaused ? "平台施工 · 已暂停" : "平台工坊 · 正在施工" };
        if (StructureWorkshop.ArenaMode && StructureWorkshop.Active) return StructureWorkshop.Phase == StructurePhase.Preview
            ? StructureWorkshop.ArenaAsking ? "克眼准备 · 等你确认" : "克眼准备 · 请补齐条件"
            : StructureWorkshop.ArenaAsking ? "克眼备料 · 等你同意采集"
            : Main.gamePaused ? "克眼准备 · 已暂停" : "克眼准备 · 备料施工中";
        if (StructureWorkshop.HousingMode && StructureWorkshop.Active) return StructureWorkshop.Phase switch {
            StructurePhase.Placing => "房屋检测 · 点击屋内", StructurePhase.Preview => "房屋修补 · 等你确认",
            _ => Main.gamePaused ? "房屋修补 · 已暂停" : "房屋修补 · 正在备料施工" };
        if (StructureWorkshop.Active) return StructureWorkshop.Phase switch {
            StructurePhase.Placing => "建筑工坊 · 选位置", StructurePhase.Preview => "建筑工坊 · 等你确认",
            _ => Main.gamePaused ? "建筑施工 · 已暂停" : "建筑工坊 · 正在施工" };
        var agent = ChatCoordinator.FindCompanion();
        if (agent == null) return "同伴暂时不在场";
        if (CompanionDialogueSystem.IsBusy) return "正在听你说 · 可随时取消";
        if (CompanionAgent.Thinking) return "在想下一步";
        if (CompanionDialogueSystem.AwaitingClarification) return "等你补充说明";
        return agent.InterfaceState switch {
            NPCs.AgentState.Planning => "正在规划", NPCs.AgentState.Replanning => "重新规划",
            NPCs.AgentState.WaitingForApproval => "等待你的同意",
            NPCs.AgentState.Executing => "正在执行任务",
            _ => agent.Mode == CompanionMode.Wait ? "原地等待" : "跟随中" };
    }
    internal static string TaskBody()
    {
        var v = CompanionTaskTracker.Current;
        if (v == null) return "还没有任务。聊天可安排砍树、挖矿或移动；基础控制不需要模型。";
        if (CompanionGardenSystem.IsTask(v.TaskId)) return CompanionGardenSystem.Describe();
        if (CompanionExpeditionSystem.IsTask(v.TaskId)) return CompanionExpeditionSystem.Describe();
        if (CompanionCombatSystem.IsTask(v.TaskId)) return CompanionCombatSystem.Describe();
        if (CraftChainSystem.IsTask(v.TaskId)) return CraftChainSystem.Describe();
        if (CompanionConstructionSystem.IsTask(v.TaskId)) return CompanionConstructionSystem.Describe();
        if (StructureWorkshop.IsTask(v.TaskId)) return StructureWorkshop.Describe();
        if (v.GoalVerified) return $"目标：{v.Goal}\n状态：{v.PhaseText}\n结果：{v.Result}";
        return $"目标：{v.Goal}\n状态：{v.PhaseText}\n动作提议：{v.Action} {v.Target}\n最近结果：{v.Result}\n已核实目标移除：{v.VerifiedEffects} 次；整项目标尚未独立核验。"
            + (v.Reason.Length == 0 ? "" : "\n" + v.Reason);
    }
    internal static string TaskState(CompanionTaskView view) => CompanionConstructionSystem.IsTask(view.TaskId)
        && CompanionConstructionSystem.Phase == ConstructionPhase.Running && (Main.gamePaused)
        ? "已暂停 · 继续游戏后施工" : view.PhaseText;
    internal static void DrawLine(SpriteBatch sb, string text, Vector2 pos, float width, Color color, float scale = 0.7f)
    {
        var font = FontAssets.MouseText.Value;
        if (font.MeasureString(text).X * scale > width)
        {
            while (text.Length > 0 && font.MeasureString(text + "…").X * scale > width) text = text[..^1];
            text += "…";
        }
        sb.DrawString(font, text, pos, color, 0, Vector2.Zero, scale, SpriteEffects.None, 0);
    }
}
