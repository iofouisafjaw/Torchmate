using TerrarAI.Content.Companion;

static class PlatformTests
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (string text in new[] { "三层、每层100格平台", "帮我搭三层木平台，每层一百格，间距六格", "3层，每层１００格平台", "build 3 layers of platforms, 100 tiles each, gap 6" })
            check(PlatformRequest.TryParse(text, out var request, out _) && request == new PlatformRequest(3, 100, 6),
                "language platform dimensions are preserved: " + text);
        PlatformRequest.TryParse("三层，每层100格平台", out var size, out _);
        var languagePlan = new PlatformBlueprint(size!.At(new(100, 200)), size.Layers, size.Spacing, _ => PlatformCell.Empty);
        var budget = PlatformMaterialBudget.Calculate(languagePlan.Needed, 0, 0, 1, 2);
        check(languagePlan.Region == new BuildRegion(100, 188, 199, 200) && languagePlan.Slots.Count == 300 && budget.WoodNeeded == 150,
            "three 100-cell rows share the existing exact blueprint geometry and recipe budget");
        foreach (string text in new[] { "搭七层，每层100格平台", "搭3层，每层161格平台", "搭3.5层，每层100格平台", "搭3层，每层-100格平台", "搭3层，每层100.5格平台", "三层平台", "三层，每层100格平台，间距3格", "三层，每层100格平台，间距13格", "三层四层，每层100格平台", "三层，每层四二九四九六七三九六格平台", "三层，每层100格玻璃平台" })
            check(PlatformRequest.TryParse(text, out var bad, out string reason) && bad == null && reason.Length > 0,
                "invalid language dimensions fail with an explanation instead of being clamped: " + text);
        foreach (string text in new[] { "搭平台", "三层每层100格平台需要多少木材？", "怎么搭三层每层100格平台", "以后不要搭三层每层100格平台" })
            check(!PlatformRequest.TryParse(text, out _, out _), "manual selection and read-only/preferences are not parsed as sized build requests: " + text);
        var region = BuildRegion.Between(new(13, 22), new(10, 10));
        check(region == new BuildRegion(10, 10, 13, 22), "reverse mouse drag normalizes an inclusive region");
        var site = new Site(12);
        var plan = new PlatformBlueprint(region, 3, 6, site.Inspect);
        check(plan.Slots.Count == 12 && plan.Slots.All(s => s.At.X >= 10 && s.At.X <= 13 && s.At.Y >= 10 && s.At.Y <= 22),
            "platform blueprint stays entirely inside the selected rectangle");
        check(plan.Slots.Select(s => s.At.Y).Distinct().SequenceEqual(new[] {22, 16, 10}),
            "platform rows are continuous, bottom-up, with the selected spacing");
        foreach (var args in new[] { (new BuildRegion(0,0,160,20),3,6), (new BuildRegion(0,0,3,96),3,6),
            (new BuildRegion(0,0,3,11),3,6), (region,7,6), (region,3,3) })
        {
            bool rejected = false;
            try { _ = new PlatformBlueprint(args.Item1,args.Item2,args.Item3,_ => throw new Exception("unbounded inspection")); }
            catch (ArgumentException) { rejected = true; }
            check(rejected,"invalid or oversized blueprint rejected before scanning the world");
        }
        site.Cells[plan.Slots[0].At] = PlatformCell.Platform;
        plan = new(region,3,6,site.Inspect);
        check(plan.Needed == 11 && plan.Reused == 1, "existing platforms reduce actual material requirements");
        var job = new PlatformBuildJob(plan);
        for (int i=0;i<12;i++) job.Step(site);
        check(job.State == PlatformJobState.Completed && job.Audit?.Passed == true && job.Placed == 11 && job.Reused == 1 && site.Available == 1,
            "verified construction consumes exactly one item per newly placed platform");
        job.Step(site); check(site.Available == 1, "completed job cannot place or charge again");
        site = new Site(20); plan = new(region,3,6,site.Inspect); job = new(plan);
        job.Step(site); job.Cancel("stop"); for(int i=0;i<20;i++) job.Step(site);
        check(job.State == PlatformJobState.Cancelled && job.Placed == 1 && site.Available == 19,
            "cancellation preserves completed work and prevents every later placement");
        site = new Site(1); job = new(new(region,3,6,site.Inspect)); job.Step(site); job.Step(site);
        check(job.State == PlatformJobState.Failed && job.Placed == 1 && site.Available == 0,
            "materials running out stops construction without free platforms");
        site = new Site(20); plan = new(region,3,6,site.Inspect); job = new(plan); job.Step(site);
        site.Cells[plan.Slots[1].At] = PlatformCell.Protected; job.Step(site);
        check(job.State == PlatformJobState.Failed && site.Cells[plan.Slots[1].At] == PlatformCell.Protected && job.Placed == 1,
            "a new obstacle stops work without overwriting it");
        site = new Site(20); plan = new(region,3,6,site.Inspect); job = new(plan);
        for(int i=0;i<11;i++) job.Step(site);
        site.Cells[plan.Slots[0].At] = PlatformCell.Empty; job.Step(site);
        check(job.State == PlatformJobState.Failed && job.Audit?.Missing == 1,
            "final audit catches an earlier platform removed during construction");
        site.Cells[plan.Slots[0].At] = PlatformCell.Protected;
        bool blocked = false;
        try { _ = new PlatformBuildJob(new(region,3,6,site.Inspect)); } catch(ArgumentException) { blocked = true; }
        check(blocked,"protected targets cannot start a construction job");
        site = new Site(12) { PlacementFails = true }; job = new(new(region,3,6,site.Inspect)); job.Step(site);
        check(job.State==PlatformJobState.Failed && job.Placed==0 && site.Available==12,
            "a placement rejected by the site stops without consumption");
        site = new Site(12) { InvalidPlacement = true }; job = new(new(region,3,6,site.Inspect)); job.Step(site);
        check(job.State==PlatformJobState.Failed && job.Placed==1 && site.Available==11,
            "an invalid created tile stops construction and retains its material debit");
    }
    private sealed class Site(int stock) : IPlatformSite
    {
        internal readonly Dictionary<BuildPoint,PlatformCell> Cells = new();
        internal bool PlacementFails, InvalidPlacement;
        public int Available { get; private set; } = stock;
        public PlatformCell Inspect(BuildPoint p) => Cells.GetValueOrDefault(p,PlatformCell.Empty);
        public bool TryPlace(BuildPoint p,out string error)
        { error="placement rejected"; if(PlacementFails || Available==0 || Inspect(p)!=PlatformCell.Empty) return false;
            Available--; Cells[p]=InvalidPlacement?PlatformCell.Protected:PlatformCell.Platform; return true; }
    }
}
