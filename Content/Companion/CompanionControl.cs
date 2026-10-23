using System.Linq;
using System;

namespace TerrarAI.Content.Companion;

public enum CompanionMode { Follow, Wait }

// Stamps are captured before I/O and checked on the game thread before committing results.
public readonly record struct ControlStamp(Guid Session, long Revision)
{
    public bool Matches(Guid session, long revision) => Session != Guid.Empty && Session == session && Revision == revision;
}

public sealed class CompanionBehaviorController
{
    public CompanionMode Mode { get; private set; }
    public float? WaitX { get; private set; }
    public void Follow() { Mode = CompanionMode.Follow; WaitX = null; }
    public void Wait(float x) { Mode = CompanionMode.Wait; WaitX = x; }
    public float WaitingVelocity(float x, float currentVelocity)
    {
        if (Mode != CompanionMode.Wait || !WaitX.HasValue) return 0;
        float delta = WaitX.Value - x;
        float desired = Math.Abs(delta) <= 8 ? 0 : Math.Clamp(delta * 0.05f, -1.5f, 1.5f);
        float next = currentVelocity + (desired - currentVelocity) * 0.4f;
        return Math.Abs(next) < 0.05f ? 0 : next;
    }
}

public enum CompanionTaskPhase { Planning, WaitingForApproval, Ready, Executing, Replanning, Stopped, Finished, Failed, Verified, Done }

public sealed record CompanionTaskView(string TaskId, string Goal, CompanionTaskPhase Phase,
    string Action = "尚无动作提议", string Target = "", string Result = "尚无执行结果",
    int VerifiedEffects = 0, bool Closed = false, string Reason = "", string? DecisionId = null, bool GoalVerified = false)
{
    public string PhaseText => Phase switch {
        CompanionTaskPhase.Planning => "正在规划", CompanionTaskPhase.WaitingForApproval => "等你同意",
        CompanionTaskPhase.Ready => "动作已准备", CompanionTaskPhase.Executing => "正在执行",
        CompanionTaskPhase.Replanning => "重新规划", CompanionTaskPhase.Stopped => "已取消",
        CompanionTaskPhase.Finished => "已结束 · 目标未核验", CompanionTaskPhase.Verified => "施工达标 · 蓝图已核验",
        CompanionTaskPhase.Done => "完成", _ => "未完成" };
    public string OutcomeText => Reason.Length > 0 ? Reason : Result;
    // Closed because the player stepped in (stop, wait, a new order ...), as opposed to running out of work.
    public static readonly string[] PlayerReasons = { "player_waiting", "player_follow", "player_stopped", "recalled",
        "dismissed", "companion_removed", "player_dialogue", "superseded", "player_rejected", "world_unloaded", "stale_control",
        "construction_cancelled" };
    public bool EndedByPlayer => Closed && PlayerReasons.Any(r => ReasonText(r) == Reason);

    public static string ReasonText(string reason) => reason switch {
        "player_waiting" => "你让同伴留在原地，任务已取消。",
        "player_follow" => "你让同伴继续跟随，任务已取消。",
        "player_stopped" => "你停止了任务。", "recalled" => "召回已取消任务。",
            "dismissed" or "companion_removed" => "同伴已离场，任务已取消。",
        "expedition_failed" => "探路或挖掘未完成；查看实际原因、发现和已挖部分。",
        "player_dialogue" or "superseded" => "收到新指令，旧任务已取消。",
        "player_rejected" => "你拒绝了这次动作。", "world_unloaded" => "离开世界，任务已取消。",
        "model_stopped" => "模型结束了任务；整项目标尚未独立核验。",
        "goal_reached" => "按要求的数量完成了。",
        "no_target" => "附近没找到能做的目标。", "no_more_targets" => "做了一部分，附近没有更多目标了。",
        "missing_tool" => "缺少需要的工具。",
        "step_limit" => "已达到决策次数上限，目标尚未核验。",
        "planner_error" => "规划失败，没有启动新动作。",
        "approval_expired" or "approval_expired_or_world_changed" => "确认过期或情境变化，任务已取消。",
        "preference_forbids_action" => "当前偏好禁止这次动作。",
        "preference_changed_during_execution" => "规则已改变，执行已停止。",
        "stale_control" => "控制指令已更新，旧任务已取消。",
        "construction_cancelled" => "施工已取消，保留已完成部分。",
        "construction_failed" => "施工未达标，请查看具体原因。",
        "garden_failed" => "种植／采收未完成，请查看实际原因和已完成部分。",
        "combat_failed" => "清怪未完成，请查看剩余目标或无法接近的原因。",
        _ => "任务已停止。" };
}
