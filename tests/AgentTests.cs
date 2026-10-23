using System.Collections.Generic;
using System.Linq;
using TerrarAI.Content.Companion;

static class AgentTests
{
    internal static void Run(Action<bool, string> check)
    {
        var craft = AgentProtocol.Parse("""{"tool":"craft","item":"工作台","count":1}""");
        check(craft.Tool == "craft" && craft.Item == "工作台" && craft.Count == 1 && !craft.Ends, "agent parses a craft call");
        check(AgentProtocol.Parse("""{"tool":"reply","text":"做好了"}""").Ends, "reply ends the loop");
        foreach (var bad in new[] { """{"tool":"teleport","item":"地狱"}""", """{"tool":"craft"}""",
                                    """{"tool":"reply","text":""}""", "[]" })
        {
            bool rejected = false;
            try { AgentProtocol.Parse(bad); } catch (Exception) { rejected = true; }
            check(rejected, "agent rejects an unknown tool or missing argument: " + bad);
        }
        var steps = new[] { new AgentStep("合成：木椅 ×1", "做不了：要在工作台旁边"), new AgentStep("合成：木椅 ×1", "做不了：要在工作台旁边") };
        check(AgentProtocol.Repeats(steps, new AgentCall("craft", "木椅", 1)), "a call that already failed twice is stopped");
        check(!AgentProtocol.Repeats(steps.Take(1).ToList(), new AgentCall("craft", "木椅", 1)), "one failure may be retried");
        string state = AgentProtocol.State("盖个小屋", "背包：木材×188", steps, new[] { "玩家：你好" });
        check(state.Contains("木材×188") && state.Contains("1. 合成：木椅 ×1 → 做不了") && state.Contains("玩家：你好"),
            "each step carries the goal, snapshot and every real result");
        var many = Enumerable.Range(0, AgentProtocol.MaxSteps - 1).Select(i => new AgentStep("看背包", "空")).ToList();
        check(AgentProtocol.State("x", "y", many, Array.Empty<string>()).Contains("reply 总结"), "near the step limit the model is told to wrap up");
        check(AgentProtocol.PlayerAgreedToBuy("帮我买个蠕虫诱饵", Array.Empty<AgentStep>()) &&
              !AgentProtocol.PlayerAgreedToBuy("我想打世吞", Array.Empty<AgentStep>()) &&
              AgentProtocol.PlayerAgreedToBuy("我想打世吞", new[] { new AgentStep("问你：要花 3 金买吗", "玩家回答：买吧") }),
            "spending coins needs the player's own words");
        var looked = new List<AgentStep> { new("看背包", "背包：铜币×71、凝胶×1", "inventory") };
        var again = new AgentCall("inventory");
        check(AgentProtocol.AlreadyLooked(looked, again) == 0, "a look-up already in hand is not run again");
        check(AgentProtocol.State("背包里有啥", "y", looked, Array.Empty<string>()).Contains("已经回答了玩家，就 reply"),
            "after a look-up the model is nudged to answer");
        looked.Add(new AgentStep("看背包", AgentProtocol.Seen(0), "inventory"));
        check(AgentProtocol.AlreadyLooked(looked, again) == 0 && AgentProtocol.LookupAnswer(looked, again).Contains("凝胶×1"),
            "asking a third time answers with the result itself");
        var changed = new List<AgentStep> { new("看背包", "背包：木材×5", "inventory"), new("合成：工作台 ×1", "做好了", "craft") };
        check(AgentProtocol.AlreadyLooked(changed, again) == -1, "after the world changed the look-up runs again");
        check(AgentProtocol.AlreadyLooked(looked, new AgentCall("craft", "工作台", 1)) == -1, "actions are never treated as look-ups");
        var gathering = new List<AgentStep> { new("合成：工作台 ×1", "做不了：还差 木材×10", "craft"), new("砍树 ×2", "砍好了", "chop") };
        check(AgentProtocol.PendingCraft(gathering) == ("工作台", 1), "a craft waiting on materials is remembered across gathering");
        gathering.Add(new AgentStep("合成：工作台 ×1", "做好了：工作台 ×1，放进你背包了。", "craft"));
        check(AgentProtocol.PendingCraft(gathering) == null, "a craft that succeeded is no longer pending");
        var ranOut = new CompanionTaskView("t", "砍树", CompanionTaskPhase.Stopped, Closed: true, Reason: CompanionTaskView.ReasonText("no_target"));
        var stopped = ranOut with { Reason = CompanionTaskView.ReasonText("player_stopped") };
        check(!ranOut.EndedByPlayer && stopped.EndedByPlayer, "running out of trees is a result for the agent; a player's stop ends the goal");
        var built = new List<AgentStep> {
            new("合成：工作台 ×1", "做好了：工作台 ×1，放进你背包了。", "craft"),
            new("放置：工作台", "放好了：工作台在你右边 2 格。", "place"),
            new("跟着你", "好，跟着你。", "control") };
        check(AgentProtocol.AlreadyLooked(built, new AgentCall("control", Mode: "follow")) == 2, "the same control twice in a row is not run again");
        check(AgentProtocol.Accomplished(built).Contains("做好了：工作台") && AgentProtocol.Accomplished(built).Contains("放好了"),
            "a goal that got done ends with what was done, not a failure");
        check(AgentProtocol.Accomplished(looked) == "", "look-ups alone accomplish nothing");
        var halfway = new List<AgentStep> { new("合成：木椅 ×1", "现在做不了：要站在工作台旁边", "craft"), new("放置：工作台", "放好了：工作台在你左边 2 格。", "place") };
        check(AgentProtocol.Accomplished(halfway).Contains("木椅还没做出来"), "the wrap-up never claims a craft that did not happen");
        string Names(string k) => k switch { "SilverCoin" => "银币", "Fargowiltas/OverloadWorm" => "蠕虫串烧", _ => "" };
        check(ChatTags.Strip("所有玩家物品栏中加起来有超过[i/s50:SilverCoin]", Names) == "所有玩家物品栏中加起来有超过50 银币" &&
              ChatTags.Strip("击败十轮[i:Fargowiltas/OverloadWorm]后[c/FF0000:获得]", Names) == "击败十轮蠕虫串烧后获得" &&
              ChatTags.Strip("没有标签", Names) == "没有标签",
            "chat tags in condition texts read as plain words");
        check(TaskGoal.OreWord("挖20块石块") == "石块" && TaskGoal.Parse("挖20块石块").Target == 20,
            "stone blocks are a mining target counted per block, beyond the ore cap");
        check(TaskGoal.OreWord("挖点土") == "土块" && TaskGoal.OreWord("挖铜矿") == "铜" && TaskGoal.OreWord("去沙漠挖矿") == null
              && TaskGoal.OreWord("挖黑曜石") == "黑曜石" && TaskGoal.OreWord("在雪地挖铁矿") == "铁",
            "block words only count when they name what to dig");
        check(TaskGoal.Parse("挖20处铁矿").Target == TaskGoal.MaxCount, "ore veins keep the smaller cap");
        check(AgentProtocol.State("商店在哪", "y", looked, Array.Empty<string>(), wrapUp: true).Contains("不要再调用工具"),
            "a wrap-up turn tells the model to answer instead of calling tools");
        check(AgentProtocol.Parse("{\"tool\":\"answer\"}").Tool == "answer", "answer needs no text: the player's own question is forwarded");
        check(AgentProtocol.LooksLikeQuestion("光明之魂是啥") && AgentProtocol.LooksLikeQuestion("荒漠灾虫掉落什么装备")
              && !AgentProtocol.LooksLikeQuestion("帮我砍5棵树"), "plain questions are recognised for the knowledge-base fallback");
        var loop = new AgentLoop("光明之魂是啥");
        var fwd = loop.OnModel("{\"tool\":\"answer\",\"text\":\"光明之魂是一种Boss\"}");
        check(fwd.Kind == AgentOutcome.Forward && fwd.Text == "光明之魂是啥", "answer forwards the player's words, not the model's guess");
        var bag = new AgentLoop("背包里有啥");
        var first = bag.OnModel("{\"tool\":\"inventory\"}");
        bag.Record(first.Call!, "背包：木材×40");
        check(bag.OnModel("{\"tool\":\"inventory\"}").Kind == AgentOutcome.Ask && bag.WrapUpMode,
            "a look-up asked for again turns into a reply-only turn");
        var end = bag.OnModel("{\"tool\":\"look\"}");
        check(end.Kind == AgentOutcome.Finish && end.Text.Contains("木材"), "a tool call during wrap-up ends with what is known");
        var broken = new AgentLoop("荒漠灾虫掉落什么装备");
        check(broken.OnModel(null).Kind == AgentOutcome.Ask && broken.OnModel(null).Kind == AgentOutcome.Forward,
            "a question the model keeps failing on goes to the knowledge base");
        var failing = new List<AgentStep> { new("合成：铁砧 ×1", "铁砧 ×1 现在做不了：还差 铁锭 ×5。", "craft"), new("查配方：铁砧", "铁砧 ×1：铁锭 ×5", "recipe") };
        check(AgentProtocol.AlreadyLooked(failing, new AgentCall("craft", "铁砧", 1)) == 0, "a failed action is not retried while nothing has succeeded since");
        check(AgentProtocol.Seen(failing, 0, new AgentCall("craft", "铁砧", 1)).StartsWith(AgentProtocol.TriedPrefix), "the nudge for a repeated action says it was tried");
        var cycle = new List<AgentStep> { new("合成：工作台 ×1", "做好了：工作台 ×1", "craft"), new("放置：工作台", "放好了：工作台", "place"),
            new("放置：工作台", "背包里没有工作台，先做一个。", "place"), new("合成：工作台 ×1", "做好了：工作台 ×1", "craft"), new("放置：工作台", "放好了", "place") };
        check(AgentProtocol.Overused(cycle, new AgentCall("place", "工作台")), "the same call a third time in one goal is a circle");
        check(AgentProtocol.MissingAmount(new[] { new AgentStep("合成：熔炉 ×1", "熔炉 ×1 现在做不了：还差 石块 ×20、火把 ×3。", "craft") }, "石块") == 20,
            "the amount a craft lacked is read back for gathering");
        var mineLoop = new AgentLoop("帮我做个熔炉", new[] { new AgentStep("合成：熔炉 ×1", "熔炉 ×1 现在做不了：还差 石块 ×20。", "craft") });
        check(mineLoop.OnModel("{\"tool\":\"mine\",\"ore\":\"石块\"}").Call?.Count == 20, "mining without a count digs what the craft lacks");
        check(new AgentLoop("你在这等我一下").OnModel("{\"tool\":\"control\"}").Call?.Mode == "wait", "control without a mode reads the player's words");
        var follow = new AgentLoop("跟着我");
        var go = follow.OnModel("{\"tool\":\"control\",\"mode\":\"follow\"}");
        follow.Record(go.Call!, "好，跟着你。");
        check(follow.AfterRun(go.Call!, "好，跟着你。")?.Kind == AgentOutcome.Finish, "a successful control order is the whole answer");
        var gear = new AgentLoop("随便生成一个装备");
        check(gear.AfterRun(new AgentCall("spawn_item", "铁剑", 1), "生成了 铁剑 ×1，放进你背包了（作弊）。")?.Kind == AgentOutcome.Finish
              && new AgentLoop("生成一套日耀盔甲").AfterRun(new AgentCall("spawn_item", "日耀头盔", 1), "生成了 日耀头盔 ×1")  == null,
            "one item asked for ends after one spawn; a set keeps going");
        var twice = new AgentLoop("帮我做个工作台");
        var c1 = twice.OnModel("{\"tool\":\"craft\",\"item\":\"工作台\",\"count\":1}");
        twice.Record(c1.Call!, "做好了：工作台 ×1，放进你背包了。");
        check(twice.OnModel("{\"tool\":\"craft\",\"item\":\"工作台\",\"count\":1}").Kind == AgentOutcome.Ask && twice.WrapUpMode,
            "making the same thing again right after it worked turns into a reply");
        var dry = new AgentLoop("帮我做个熔炉", new[] {
            new AgentStep("挖矿：石块 ×20", "已取消：附近没找到石块。", "mine"), new AgentStep("砍树 ×2", "完成：砍了 2 棵树", "chop"),
            new AgentStep("挖矿：附近的矿 ×20", "已取消：附近没找到能挖的矿。", "mine") });
        check(dry.OnModel("{\"tool\":\"mine\",\"ore\":\"铜\"}").Kind == AgentOutcome.Ask && dry.WrapUpMode,
            "after two empty gathering trips the agent stops and explains");
        var followUp = new AgentLoop("那它掉什么");
        followUp.Prompt("", new[] { "玩家：第一个boss打谁", "你：先打克苏鲁之眼。" });
        check(followUp.Question().Contains("克苏鲁之眼") && followUp.Question().EndsWith("那它掉什么"),
            "a follow-up question carries what it points at to the knowledge base");
        check(new AgentLoop("光明之魂是啥").Question() == "光明之魂是啥", "a self-contained question goes as it is");
        var trips = new List<AgentStep> { new("合成：铁锭 ×3", "铁锭 ×3 现在做不了：还差 铁矿 ×9。", "craft"),
            new("挖矿：铁 ×8", "完成：挖了 8 处\n做铁锭：还差 铁矿 ×1。", "mine") };
        var tripLoop = new AgentLoop("帮我炼3个铁锭", trips);
        var second = tripLoop.OnModel("{\"tool\":\"mine\",\"ore\":\"铁\"}");
        check(second.Kind == AgentOutcome.Run && second.Call?.Count == 1, "a second gathering trip fetches only what is still missing");
        check(AgentProtocol.Schema.GetProperty("properties").GetProperty("tool").GetProperty("enum").GetArrayLength() == AgentProtocol.Tools.Length,
            "schema and tool list agree");
    }
}
