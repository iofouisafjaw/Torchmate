using TerrarAI.Content.Companion;

static class PlatformSupplyTests
{
    internal static void Run(Action<bool, string> check)
    {
        var budget = PlatformMaterialBudget.Calculate(11, 2, 3, 1, 2);
        check(budget == new PlatformMaterialBudget(9, 5, 5, 2, 10), "supply budget deducts owned platforms/wood and rounds to whole recipe batches");
        budget = PlatformMaterialBudget.Calculate(8, 20, 0, 3, 4);
        check(budget.Batches == 0 && budget.WoodShortfall == 0, "enough finished platforms need no wood or crafting");
        var site = new Site(); var job = New(site);
        job.Step(site);
        check(job.Stage == SupplyStage.Gathering && site.Crafts == 0, "a removed tree without inventory gains cannot satisfy material preparation");
        site.Yield = 3;
        job.Step(site); job.Step(site);
        check(job.Stage == SupplyStage.Crafting && site.Wood == 3 && site.Harvests == 2, "actual received wood ends gathering without demanding another tree");
        job.Step(site);
        check(job.Stage == SupplyStage.Building && job.WoodConsumed == 2 && job.Crafted == 4 && site.Wood == 1,
            "supply chain crafts only the blueprint deficit and preserves leftover wood");
        for (int i = 0; i < 4; i++) job.Step(site);
        check(job.Stage == SupplyStage.Completed && job.Build.Audit?.Passed == true && site.Available == 0,
            "supply chain reuses placement and final tile audit before completion");
        job.Step(site);
        check(site.Crafts == 1 && site.Harvests == 2, "completed supply cannot harvest or charge twice");
        site = new Site { Wood = 5, Available = 3 }; job = New(site);
        for (int i = 0; i < 10; i++) job.Step(site);
        check(job.Stage == SupplyStage.Completed && site.Wood == 4 && site.Available == 1 && site.Harvests == 0,
            "partial platform stock uses one recipe batch and keeps surplus output");
        site = new Site { Available = 4 }; job = New(site);
        for (int i = 0; i < 6; i++) job.Step(site);
        check(job.Stage == SupplyStage.Completed && site.Crafts == 0 && site.Harvests == 0, "finished-platform-only chain skips harvesting and crafting");
        site = new Site { Wood = 2 }; job = New(site); job.Step(site); site.Wood = 0; job.Step(site);
        check(job.Stage == SupplyStage.Failed && site.Crafts == 0, "materials removed between preparation and crafting fail without free output");
        site = new Site { Wood = 2, CraftAllowed = false }; job = New(site); job.Step(site); job.Step(site);
        check(job.Stage == SupplyStage.Failed && site.Wood == 2 && site.Available == 0, "unavailable recipe or output space stops before inventory mutation");
        site = new Site { Wood = 2, BadReceipt = true }; job = New(site); job.Step(site); job.Step(site);
        check(job.Stage == SupplyStage.Failed && job.Build.Placed == 0, "incorrect craft inventory receipt cannot start construction");
        site = new Site { Wood = 2 }; job = New(site); job.Step(site); job.Step(site); job.Step(site);
        job.Cancel(site, "stop"); int remaining = site.Available;
        for (int i = 0; i < 8; i++) job.Step(site);
        check(job.Stage == SupplyStage.Cancelled && job.Build.Placed == 1 && site.Available == remaining, "cancelling supply preserves partial work and prevents later effects");
        site = new Site { Wood = 2 }; job = New(site); job.Step(site); job.Step(site);
        for (int i = 0; i < 3; i++) job.Step(site);
        site.Cells.Clear(); job.Step(site);
        check(job.Stage == SupplyStage.Failed && job.Build.Audit?.Missing == 3, "supply completion rejects previously placed platforms removed before the audit");
        site = new Site { HarvestResult = HarvestStep.WaitingForApproval }; job = New(site);
        for (int i = 0; i < 3; i++) job.Step(site);
        check(job.Stage == SupplyStage.Gathering && site.Crafts == 0 && job.Build.Placed == 0, "waiting for harvest approval cannot advance to craft or build");
        job.Cancel(site, "reject"); job.Step(site);
        check(job.Stage == SupplyStage.Cancelled && site.Cancelled, "rejection cancels pending harvest as well as construction");
    }
    private static PlatformSupplyJob New(Site site) => new(new PlatformBlueprint(new(10, 10, 13, 10), 1, 6, site.Inspect), 1, 2);
    private sealed class Site : IPlatformSupplySite
    {
        public Dictionary<BuildPoint, PlatformCell> Cells = new();
        public int Available { get; set; }
        public int Wood { get; set; }
        public int Yield, Crafts, Harvests;
        public bool CraftAllowed = true, BadReceipt, Cancelled;
        public HarvestStep HarvestResult = HarvestStep.Finished;
        public PlatformCell Inspect(BuildPoint point) => Cells.GetValueOrDefault(point, PlatformCell.Empty);
        public bool TryPlace(BuildPoint point, out string error)
        { error = ""; Available--; Cells[point] = PlatformCell.Platform; return true; }
        public HarvestStep Harvest(out string message)
        { message = "fixture"; Harvests++; Wood += Yield; return HarvestResult; }
        public bool Craft(int batches, out string message)
        {
            message = "fixture";
            if (!CraftAllowed) return false;
            Crafts++; Wood -= batches; Available += batches * 2 + (BadReceipt ? 1 : 0); return true;
        }
        public void CancelHarvest() => Cancelled = true;
    }
}
