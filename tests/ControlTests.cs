using TerrarAI.Content.Companion;

static class ControlTests
{
    internal static void Run(Action<bool, string> check)
    {
        var world = Guid.NewGuid(); var old = new ControlStamp(world, 12);
        check(old.Matches(world, 12) && !old.Matches(world, 13), "manual command revision invalidates an older model result");
        check(!old.Matches(Guid.NewGuid(), 12) && !new ControlStamp(Guid.Empty, 0).Matches(Guid.Empty, 0),
            "world identity and a valid session are required for model result commits");
        var behavior = new CompanionBehaviorController();
        behavior.Wait(120);
        check(behavior.Mode == CompanionMode.Wait && behavior.WaitX == 120 && behavior.WaitingVelocity(120, 0) == 0,
            "wait stores its own position and stays still at its anchor");
        check(behavior.WaitingVelocity(200, 0) < 0 && behavior.WaitingVelocity(50, 0) > 0,
            "displacement during wait steers toward its anchor rather than the player");
        behavior.Follow();
        check(behavior.Mode == CompanionMode.Follow && behavior.WaitX == null,
            "continue-follow clears the waiting anchor");
        var ended = new CompanionTaskView("task", "采集", CompanionTaskPhase.Finished, VerifiedEffects: 1, Closed: true);
        check(ended.PhaseText.Contains("目标未核验"), "ending with verified action effects does not claim goal success");
        check(CompanionTaskView.ReasonText("player_waiting").Contains("取消") &&
            CompanionTaskView.ReasonText("model_stopped").Contains("未独立核验"), "task cards distinguish manual cancellation from an unverified model finish");
        var one = TaskGoal.Parse("帮我砍附近的一棵树，完成后告诉我");
        check(one.Kind == "chop" && one.Target == 1 && one.Explicit, "\"一棵树\" is a chop goal of exactly one");
        check(TaskGoal.Parse("挖 3 个铜矿") is { Kind: "mine", Target: 3 }, "digit counts are read for mining");
        check(TaskGoal.Parse("砍十二棵树").Target == TaskGoal.MaxCount && TaskGoal.Parse("砍两棵树").Target == 2,
            "Chinese numerals are read and capped");
        check(TaskGoal.Parse("帮我挖附近的铜矿") is { Kind: "mine", Target: 1, Explicit: false }, "an uncounted gather means one target");
        check(TaskGoal.Parse("把附近的树都砍了").Target == TaskGoal.MaxCount && !TaskGoal.Parse("跟我走").Countable,
            "\"all\" uses the cap; non-gathering commands are not counted");
        foreach (var q in new[] { "砍完了吗", "你砍了几棵树？", "挖完没有", "你还没砍完吗", "在干嘛", "清完了吗", "你打了几个怪", "打完了吗", "保护得怎么样了" })
            check(StatusQuery.IsStatusQuestion(q), "status question: " + q);
        foreach (var q in new[] { "帮我砍附近的一棵树，完成后告诉我", "再砍两棵", "以后采集前先问我",
            "按我当前世界的进度和背包，建议下一步做什么？", "你好吗" })
            check(!StatusQuery.IsStatusQuestion(q), "not a status question: " + q);
        check(TaskGoal.OreWord("帮我挖 3 个铜矿") == "铜" && TaskGoal.OreWord("挖铂金") == "铂金" && TaskGoal.OreWord("砍树") == null,
            "the named ore is read (铂金 is not 金)");
        check(TaskGoal.Anchor("砍我旁边的树") == "player" && TaskGoal.Anchor("砍左边那棵") == "left" &&
            TaskGoal.Anchor("砍树") == "companion", "the player can point at where to work");
        foreach (var q in new[] { "按我当前世界的进度和背包，建议下一步做什么？", "那我们第一个要打的首领是啥，奖励又是啥",
            "克眼怎么打", "这些东西的效果都是啥", "可疑眼球怎么合成", "生命水晶在哪找" })
            check(AdviceQuery.IsAdviceQuestion(q), "advice question: " + q);
        foreach (var q in new[] { "帮我砍附近的一棵树，完成后告诉我", "砍完了吗", "你好吗", "去挖矿", "以后采集前先问我", "跟我走" })
            check(!AdviceQuery.IsAdviceQuestion(q), "not an advice question: " + q);
    }
}
