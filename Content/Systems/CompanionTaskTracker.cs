using TerrarAI.Content.Companion;
using Terraria.ModLoader;

namespace TerrarAI.Content.Systems;

// One active/last task for the current visit; UI reads this immutable view, never the action queue.
public sealed class CompanionTaskTracker : ModSystem
{
    internal static CompanionTaskView? Current { get; private set; }
    internal static int Revision { get; private set; }
    internal static void Begin(string taskId, string goal)
    { Set(new(taskId, goal, CompanionTaskPhase.Planning)); CompanionRecoverySystem.Began(Current!); }
    internal static void Update(string? taskId, CompanionTaskPhase phase, string? action = null, string? target = null,
        string? result = null, int? verified = null, string? decisionId = null)
    {
        var view = Current;
        if (view == null || view.TaskId != taskId || view.Closed) return;
        Set(view with { Phase = phase, Action = action ?? view.Action, Target = target ?? view.Target,
            Result = result ?? view.Result, VerifiedEffects = verified ?? view.VerifiedEffects, DecisionId = decisionId ?? view.DecisionId });
    }
    internal static void Close(string? taskId, string reason, int verified)
    {
        var view = Current;
        if (view == null || view.TaskId != taskId || view.Closed) return;
        var phase = reason == "model_stopped" ? CompanionTaskPhase.Finished
            : reason is "planner_error" or "step_limit" or "missing_tool" or "no_target" or "no_more_targets" or "construction_failed" or "combat_failed" or "expedition_failed" or "garden_failed" ? CompanionTaskPhase.Failed : CompanionTaskPhase.Stopped;
        Set(view with { Phase = phase, Closed = true, Reason = CompanionTaskView.ReasonText(reason), VerifiedEffects = verified });
        CompanionRecoverySystem.Closed(Current!,reason);
    }
    // Gathering goal reached: the count comes from verified tile removals, not from the model.
    internal static void Finish(string? taskId, string result, int verified)
    {
        var view = Current;
        if (view == null || view.TaskId != taskId || view.Closed) return;
        Set(view with { Phase = CompanionTaskPhase.Done, Closed = true, Result = result, Reason = "",
            VerifiedEffects = verified, GoalVerified = true });
        CompanionRecoverySystem.Closed(Current!);
    }
    // The haul is only known after drops are picked up, a moment after the task closes.
    internal static void SetResult(string? taskId, string result)
    {
        var view = Current;
        if (view == null || view.TaskId != taskId) return;
        Set(view with { Result = result });
    }
    internal static void CompleteVerified(string? taskId, string result)
    {
        var view = Current;
        if (view == null || view.TaskId != taskId || view.Closed) return;
        Set(view with { Phase = CompanionTaskPhase.Verified, Closed = true, Result = result, Reason = "", GoalVerified = true });
        CompanionRecoverySystem.Closed(Current!);
    }
    private static void Set(CompanionTaskView? value)
    { if (Current == value) return; Current = value; Revision++; CompanionCollaborationSystem.Observe(value); }
    public override void OnWorldLoad() => Set(null);
    public override void OnWorldUnload() => Set(null);
    public override void Unload() => Set(null);
}
