using TerrarAI.Content.Companion;

static class CollaborationTests
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (var scenario in new[] { "eye", "housing" })
        {
            var p = new CollaborationPlan(scenario, 2);
            check(p.Steps.Count == 2 && !p.Complete && p.Next == p.Steps[0], scenario + " shares ordered collaborative ledger");
            string step = p.Next!.Id;
            check(p.Bind(step, "old", p.Revision), scenario + " binds the native preview");
            int old = p.Revision;
            check(!p.Observe("old", old, CollaborationState.Verified, ""), scenario + " cannot accept empty verification");
            p.Observe("old", old, CollaborationState.Running, "placed three, not complete");
            p.Assign(step, CollaborationOwner.Player);
            check(!p.Observe("old", old, CollaborationState.Verified, "late success") && !p.Complete,
                scenario + " reassignment rejects stale success and revokes execution binding");
            check(p.Next!.Owner == CollaborationOwner.Player && p.Next.Evidence.Contains("three"), scenario + " handoff retains partial progress");
            check(p.Bind(step, "new", p.Revision), scenario + " handoff still needs new native binding");
            p.Pause("player stopped");
            check(!p.Bind(step, "bad", p.Revision) && !p.Observe("new", p.Revision, CollaborationState.Verified, "late"),
                scenario + " paused projects reject callbacks and new execution");
            p.Resume(); p.Bind(step, "retry", p.Revision);
            p.Observe("retry", p.Revision, CollaborationState.Failed, "station lost; saved progress");
            check(p.Next!.State == CollaborationState.Failed && !p.Complete, scenario + " failure is not task completion");
            p.Bind(step, "retry2", p.Revision); p.Observe("retry2", p.Revision, CollaborationState.Verified, "native audited original site");
            check(p.Next?.Id == p.Steps[1].Id && p.Steps[0].State == CollaborationState.Verified, scenario + " resumes only unfinished work");
            bool rejected = false;
            try { p.Assign(step, CollaborationOwner.Companion); } catch (ArgumentException) { rejected = true; }
            check(rejected, scenario + " cannot silently redo completed work");
            string second = p.Next!.Id; p.Bind(second, "last", p.Revision); p.Observe("last", p.Revision, CollaborationState.Verified, "final stage audit");
            check(p.Complete, scenario + " completes only after both stage receipts");
            p.RecheckFailed(step, "live world changed");
            check(!p.Complete && p.Next!.Id == step, scenario + " fresh failed audit invalidates historical completion");
        }
        var house = new CollaborationPlan("housing"); house.Bind("materials", "supply", house.Revision);
        int oldRevision = house.Revision; house.Resize(3);
        check(house.Rooms == 3 && !house.Observe("supply", oldRevision, CollaborationState.Verified, "old budget"), "resize revokes obsolete quantities and approval bindings");
        house.Bind("materials", "s2", house.Revision); house.Observe("s2", house.Revision, CollaborationState.Verified, "stock");
        house.Bind("housing", "selected", house.Revision);
        bool blocked = false; try { house.Resize(2); } catch (ArgumentException) { blocked = true; }
        check(blocked && house.Rooms == 3, "selected construction footprint cannot expand through resize");
        var bounded = new CollaborationPlan("eye");
        for (int i = 0; i < 200; i++) bounded.Pause("interrupt " + i);
        check(bounded.Journal.Count == 128 && bounded.EventCount == 201, "bounded history retains monotonic logging cursor");
        var template = new List<CollaborationStep> { new("a", "调查", "query"), new("b", "分工处理", "custom"), new("c", "复查", "audit") };
        var generic = new CollaborationPlan("custom adapter", template); template.Clear();
        foreach (string step in new[] { "a", "b", "c" })
        { generic.Bind(step, "native:" + step, generic.Revision); generic.Observe("native:" + step, generic.Revision, CollaborationState.Verified, "adapter verified " + step); }
        check(generic.Complete && generic.Steps.Count == 3 && generic.Describe().Contains("custom adapter"), "third adapter uses unchanged generic ledger and owns a defensive copy of its template");
        bool invalidTemplate = false;
        try { _ = new CollaborationPlan("untrusted template", new[] { new CollaborationStep("x", "work", "custom", State: CollaborationState.Verified) }); }
        catch (ArgumentException) { invalidTemplate = true; }
        check(invalidTemplate, "templates cannot inject preverified work or approval bindings");
        foreach (string bad in new[] { "boss", "", "craft" })
        {
            bool rejected = false; try { _ = new CollaborationPlan(bad); } catch (ArgumentException) { rejected = true; }
            check(rejected, "unsupported scenario cannot become executable: " + bad);
        }
        foreach (var (text, op, target, rooms) in new[] {
            ("一起准备打克眼", "start", "eye", 1), ("协作扩建二间NPC住房", "start", "housing", 2),
            ("扩建3间NPC住房", "start", "housing", 3), ("扩建两间NPC住房", "start", "housing", 2), ("药水我来", "assign", "药水", 1),
            ("我负责备料", "assign", "备料", 1), ("住房改成你负责", "assign", "住房", 1),
            ("住房改成三间", "resize", "", 3), ("继续协作", "continue", "", 1),
            ("我做完了", "audit", "", 1), ("暂停协作", "pause", "", 1), ("现在做到哪了", "status", "", 1),
            ("一起准备克眼，药水我自己做", "clarify", "", 1), ("协作扩建12间住房", "clarify", "", 1),
            ("一起准备克眼并扩建住房", "clarify", "", 1), ("一起准备克眼，别拆家", "clarify", "", 1), ("扩建20个房间", "clarify", "", 1) })
        {
            var parsed = CollaborationRequest.Parse(text, true);
            check(parsed?.Operation == op && parsed.Target == target && parsed.Rooms == rooms, "collaboration intent preserves scope: " + text);
        }
        foreach (string query in new[] { "协作准备克眼怎么做", "一起准备克眼？", "查背包", "药水我来" })
            check(CollaborationRequest.Parse(query, false) == null, "query or out-of-project phrase does not launch project: " + query);
    }
}
