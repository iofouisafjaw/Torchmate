using System;
using System.Collections.Generic;
using System.Linq;

namespace TerrarAI.Content.Companion;

public enum PolicyResult { Allow, Ask, Deny }

public static class CompanionPolicy
{
    public static PolicyResult Check(IEnumerable<LearnedPreference> preferences, CompanionDecision action, bool approved = false, bool? day = null)
    {
        if (action.Type is not ("mine" or "chop")) return PolicyResult.Allow;
        var list = preferences.ToList();
        string key = action.Type == "mine" ? "mining" : "chopping";
        bool Applies(LearnedPreference p) => p.When == "any" || p.When == (day == true ? "day" : day == false ? "night" : "unknown");
        if (list.Any(p => p.Key == key && p.Value == "forbidden" && Applies(p))) return PolicyResult.Deny;
        if (day == null && list.Any(p => (p.Key == key || p.Key == "gather_confirmation") && p.When != "any"))
            return PolicyResult.Ask; // Unknown context cannot silently become permission.
        return CheckGatherConfirmation(list,approved,day);
    }
    public static PolicyResult CheckGatherConfirmation(IEnumerable<LearnedPreference> preferences,bool approved=false,bool? day=null)
    {
        var list=preferences.ToList();
        if(day==null&&list.Any(p=>p.Key=="gather_confirmation"&&p.When!="any"))return PolicyResult.Ask;
        bool Applies(LearnedPreference p)=>p.When=="any"||p.When==(day==true?"day":day==false?"night":"unknown");
        var confirmation = list.Where(p => p.Key == "gather_confirmation" && Applies(p))
            .OrderByDescending(p => p.When != "any").ThenByDescending(p => p.UpdatedAt).FirstOrDefault();
        return !approved && confirmation?.Value == "required"
            ? PolicyResult.Ask : PolicyResult.Allow;
    }
}

// A confirmation authorizes exactly one decision in one task/world session, briefly.
public sealed record ApprovalTicket(Guid Session, string TaskId, string DecisionJson, DateTimeOffset Expires,
    long RuleRevision = 0, bool? Day = null)
{
    public bool Matches(Guid session, string? taskId, string json, DateTimeOffset now, long revision = 0, bool? day = null) =>
        Session == session && TaskId == taskId && DecisionJson == json && now < Expires && RuleRevision == revision && Day == day;
}
