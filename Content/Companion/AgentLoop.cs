using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TerrarAI.Content.Companion;

// What the game should do after a model reply. Run: execute Call (a world tool) and Record its result.
// Ask: request the next step from the model. Finish: say Text and end. Question: say Text and wait for
// the player's answer. Forward: hand Text (the player's question) to the knowledge base and end.
public enum AgentOutcome { Run, Ask, Finish, Question, Forward }

public sealed record AgentDecision(AgentOutcome Kind, AgentCall? Call = null, string Text = "", bool Warn = false);

// The agent's control flow without the game: the same object runs inside Terraria (CompanionAgent) and in
// the offline evaluation (evals/), so what is measured is exactly what plays.
public sealed class AgentLoop
{
    public string Goal { get; }
    public List<AgentStep> Steps { get; }
    public bool WrapUpMode { get; private set; }
    public bool RecoveryMode { get; private set; }
    public bool RecoveryDecisionOnly {get;private set;}
    private bool _canReplan;
    private TaskFailureReceipt? _failureReceipt;
    public bool ObservationOnly {get;private set;}
    public JsonElement Schema=>ObservationOnly?ObservationPolicy.Schema:RecoveryMode?RecoveryPolicy.Schema(_canReplan,RecoveryDecisionOnly):AgentProtocol.Schema;
    public void Recover(TaskFailureReceipt receipt)
    {
        RecoveryMode=true;_canReplan=receipt.CanReplan;_failureReceipt=receipt;RecoveryDecisionOnly=false;WrapUpMode=false;_badReplies=0;
        Steps.Add(new AgentStep("长任务结果："+receipt.Kind,receipt.Describe(),"long_task_result"));
    }
    private int _badReplies;
    private string _fallback = "";
    private bool _fallbackWarn;
    private List<AgentStep> Evidence=>RecoveryMode?Steps.Skip(Steps.FindLastIndex(s=>s.Tool=="long_task_result")+1).ToList():Steps;

    public AgentLoop(string goal, IEnumerable<AgentStep>? earlier = null)
    {
        Goal = goal;
        ObservationOnly=ObservationPolicy.Matches(goal);
        Steps = earlier?.ToList() ?? new List<AgentStep>();
    }

    // Before each model request: too many steps ends the goal instead.
    public AgentDecision? BeforeRequest() => Evidence.Count >= AgentProtocol.MaxSteps
        ? new AgentDecision(AgentOutcome.Finish, Text: "步骤太多了，先停在这里：" + (Steps.Count > 0 ? Steps[^1].Result : ""), Warn: true)
        : null;

    public string Prompt(string snapshot, IReadOnlyList<string> recentDialogue)
    {
        _dialogue = recentDialogue;
        return AgentProtocol.State(Goal, snapshot, Steps, recentDialogue, WrapUpMode,RecoveryMode?Evidence.Count:null)+(RecoveryMode?"\n\n"+RecoveryPolicy.Prompt:"")+
            (RecoveryDecisionOnly?"\n补查已重复且没有新执行证据。原任务仍未验收。现在不能再查询；需要继续原目标时用replan生成待确认预览，不能重规划时ask请玩家检查并重新下令，或reply/control(stop)停止。":"")+
            (ObservationOnly?"\n玩家本次只要求检查状态、解释失败或停止。只能查询、回复、询问或control(stop)，不能replan或启动新任务。":"");
    }

    private IReadOnlyList<string> _dialogue = Array.Empty<string>();
    private static readonly System.Text.RegularExpressions.Regex Pointing = new("它|他|她|那个|这个|那玩意|这玩意|上面|刚才");

    // The question for the knowledge base: the player's words, plus what "它/那个" points at when it is a follow-up.
    public string Question()
    {
        if (!Pointing.IsMatch(Goal)) return Goal;
        var context = _dialogue.TakeLast(2).Where(l => !l.EndsWith(Goal)).ToList();
        return context.Count == 0 ? Goal : "（上文 " + string.Join(" ", context) + "）" + Goal;
    }

    public void Record(AgentCall call, string result) => Steps.Add(new AgentStep(call.Label, result, call.Tool));
    public void Answer(string question,string text)
    {
        Steps.Add(new AgentStep("问你："+question,"玩家回答："+text.Trim()));RecoveryDecisionOnly=false;
        if(ObservationPolicy.Matches(text)){ObservationOnly=true;_canReplan=false;}
    }
    private string RecoveryStop()
    {
        var last=Evidence.LastOrDefault(s=>AgentProtocol.IsLookup(s.Tool));
        return RecoveryPolicy.StopText+"已完成部分保留；本次查询阶段未发出新的备料、合成或施工指令。"+
            (_failureReceipt==null?"":"\n失败原因："+_failureReceipt.Reason)+
            (last==null?"":"\n最近补查结果："+last.Result);
    }

    // The model's reply arrived. `failure` is set when the request itself failed (no reply text).
    public AgentDecision OnModel(string? raw, string? failure = null)
    {
        if (failure != null)
            return new AgentDecision(AgentOutcome.Finish, Text: "模型没有回应（" + failure + "）。看看「启动模型」窗口还开着没。", Warn: true);
        AgentCall call;
        try { call = AgentProtocol.Parse(raw ?? ""); }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            // Schema-constrained output rarely breaks; one retry, then a plain question goes to the knowledge base.
            if (++_badReplies <= 1) return new AgentDecision(AgentOutcome.Ask);
            if (Steps.Count == 0 && AgentProtocol.LooksLikeQuestion(Goal)) return new AgentDecision(AgentOutcome.Forward, Text: Question());
            return new AgentDecision(AgentOutcome.Finish, Text: "我没想明白下一步，先停下了。换个说法再试试？", Warn: true);
        }
        call = Fill(call);
        if(ObservationOnly&&!ObservationPolicy.Allows(call))
            return new AgentDecision(AgentOutcome.Finish,Text:ObservationPolicy.Blocked,Warn:true);
        if(call.Tool=="replan"&&!RecoveryMode)
            return new AgentDecision(AgentOutcome.Finish,Text:"当前没有可恢复的失败任务，请重新说明目标。",Warn:true);
        if(RecoveryMode&&!RecoveryPolicy.Allows(call))
        {
            Steps.Add(new AgentStep(call.Label,RecoveryPolicy.Blocked,"recovery_blocked"));
            return new AgentDecision(AgentOutcome.Ask);
        }
        if(RecoveryDecisionOnly&&!RecoveryPolicy.AllowsDecision(call,_canReplan))
        {Steps.Add(new AgentStep(call.Label,"补查已重复；请选择原任务的新预览、询问或停止。","recovery_blocked"));return new AgentDecision(AgentOutcome.Ask);}
        // Told to wrap up and still reaching for tools: answer from what we have.
        if (WrapUpMode && call.Tool is not ("reply" or "ask" or "answer"))
            return new AgentDecision(AgentOutcome.Finish, Text: _fallback, Warn: _fallbackWarn);
        // A failed long task may have changed both inventory and terrain. Keep history for the
        // model, but stale pre-failure lookups/successes cannot suppress fresh observations.
        var evidence=Evidence;
        if (AgentProtocol.AlreadyLooked(evidence, call,RecoveryMode) is int seen and >= 0)
        {
            if(RecoveryMode&&AgentProtocol.IsLookup(call.Tool))
            {RecoveryDecisionOnly=true;Steps.Add(new AgentStep(call.Label,"此项已查过，未执行新操作；原任务仍未验收。请选择replan新预览、ask或停止。","recovery_blocked"));return new AgentDecision(AgentOutcome.Ask);}
            // Asked twice for a result already in hand: one nudge, then a reply-only turn.
            // A look-up asked for again: the answer is already in hand, so this turn only replies.
            // (A nudge first rarely helped; the model just reached for another look-up.)
            if (AgentProtocol.IsLookup(call.Tool) || evidence.Any(s => AgentProtocol.IsSeen(s.Result)))
                return WrapUp(AgentProtocol.LookupAnswer(evidence, call,RecoveryMode), false, call);
            Steps.Add(new AgentStep(call.Label, AgentProtocol.Seen(evidence, seen, call), call.Tool));
            return new AgentDecision(AgentOutcome.Ask, call);
        }
        // Gathering keeps coming back empty: there is nothing of that kind around here.
        if (call.Tool is "chop" or "mine" && evidence.Count(s => s.Tool is "chop" or "mine" && s.Result.StartsWith("已取消")) >= 2)
            return WrapUp("附近找不到能采集的东西：" + evidence.Last(s => s.Result.StartsWith("已取消")).Result.Split('\n')[0]
                + "。换个地方（往下挖、去别的地形）再叫我。", true, call);
        // Doing again what just succeeded: the request is most likely met; time to say so.
        if (AgentProtocol.RepeatsSuccess(evidence, call))
            return WrapUp(AgentProtocol.Accomplished(evidence) is { Length: > 0 } done0 ? done0 : evidence[^1].Result.Split('\n')[0], false, call);
        if (AgentProtocol.Repeats(evidence, call) || AgentProtocol.Overused(evidence, call))
        {
            string done = AgentProtocol.Accomplished(evidence);
            string last = evidence[^1].Result.Split('\n')[0];
            return done.Length > 0
                ? WrapUp(AgentProtocol.IsSuccess(last) ? done : done + $"不过「{call.Label}」没成功：{last}", false, call)
                : WrapUp("这一步试了几次都不行：" + last, true, call);
        }
        return call.Tool switch
        {
            "reply" => new AgentDecision(AgentOutcome.Finish, call, RecoveryMode?RecoveryStop():call.Text),
            "ask" => new AgentDecision(AgentOutcome.Question, call, (RecoveryMode?"原任务仍未通过最终验收。":"")+call.Text),
            // The knowledge base gets the player's own words: the model tends to write its own (often wrong)
            // answer into this field, and the base would then "answer" that.
            "answer" => new AgentDecision(AgentOutcome.Forward, call, Question()),
            _ => new AgentDecision(AgentOutcome.Run, call)
        };
    }

    // Arguments the model often leaves out, filled from what the player said and what the last craft lacked.
    private AgentCall Fill(AgentCall call)
    {
        if (call.Tool == "control" && call.Mode.Length == 0)
            call = call with { Mode = System.Text.RegularExpressions.Regex.IsMatch(Goal, "等|留|别动|待着|呆着") ? "wait"
                : System.Text.RegularExpressions.Regex.IsMatch(Goal, "回来|过来|召回") ? "recall"
                : System.Text.RegularExpressions.Regex.IsMatch(Goal, "停") ? "stop" : "follow" };
        if (call.Count == 0 && call.Tool == "chop" && AgentProtocol.MissingAmount(Steps, "木材") is int wood and > 0)
            call = call with { Count = Math.Clamp((wood + 9) / 10, 1, TaskGoal.MaxCount) };   // about 10 wood a tree
        if (call.Count == 0 && call.Tool == "mine")
        {
            string ore = call.Ore.Replace("矿", "").Trim();
            string? block = TaskGoal.OreWord("挖" + ore);
            int lack = new[] { block ?? "", ore + "矿", ore }.Select(n => AgentProtocol.MissingAmount(Steps, n)).Max();
            if (lack > 0) call = call with { Count = TaskGoal.IsBlock(block) ? Math.Min(lack, TaskGoal.MaxBlocks) : Math.Min(lack, TaskGoal.MaxCount) };
        }
        return call;
    }

    // One last model turn that may only reply; `fallback` is said if the model still reaches for a tool.
    private AgentDecision WrapUp(string fallback, bool warn, AgentCall skipped)
    {
        WrapUpMode = true;
        _fallback = fallback;
        _fallbackWarn = warn;
        return new AgentDecision(AgentOutcome.Ask, skipped);
    }

    private static readonly System.Text.RegularExpressions.Regex Several = new("些|几|套|全|都|各|和|、|\\d|十|两|三|四|五|多");

    // After a tool ran: some results are already the whole answer, so the goal ends here without another
    // model turn (the model tends to repeat these instead of replying).
    public AgentDecision? AfterRun(AgentCall call, string result)
    {
        bool ok = AgentProtocol.IsSuccess(result);
        if (call.Tool == "control" && ok) return new AgentDecision(AgentOutcome.Finish, Text: result);
        if (call.Tool == "structure") return new AgentDecision(AgentOutcome.Finish, Text: result);
        if (call.Tool == "spawn_item" && ok && !Several.IsMatch(Goal)) return new AgentDecision(AgentOutcome.Finish, Text: result);
        return null;
    }
}
