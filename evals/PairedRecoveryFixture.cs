using System.Text.Json;
using TerrarAI.Content.Companion;

namespace TerrarAI.Evals;

// Explicit, finite offline sites. No native Terraria physics/catalog is implied.
public sealed class PairedRecoveryCase
{
    public string Id { get; set; } = "";
    public string PairId { get; set; } = "";
    public string Family { get; set; } = "";
    public string Mechanism { get; set; } = "";
    public string Variant { get; set; } = "";
    public string Goal { get; set; } = "";
    public int Count { get; set; }
    public int Completed { get; set; }
    public bool SiteAccessible { get; set; } = true;
    public bool ChestObserved { get; set; } = true;
    public Dictionary<int, int> Bag { get; set; } = new();
    public Dictionary<int, int> Chests { get; set; } = new();
}

// This is the entire observation available to the rule, and is rendered verbatim for models.
// Feasibility, expected actions and counterpart IDs never enter this object.
public sealed record PairedObservation(string Family, int Total, int VerifiedCompleted, bool SiteAccessible,
    IReadOnlyDictionary<string, int> RemainingMaterials, IReadOnlyDictionary<string, int> Backpack,
    IReadOnlyDictionary<string, int>? NearbyChests);

public sealed class PairedRecoveryFixture
{
    public static readonly string[] Names = { "", "木材", "石块", "火把", "工作台", "熔炉", "铁锭",
        "铁头盔", "铁链甲", "铁护胫", "木椅", "木墙", "木门", "木桌", "木平台", "凝胶", "太阳花种子" };
    public PairedRecoveryCase Case { get; }
    private readonly Dictionary<int, int> _bag, _chests;
    private bool _chestObserved;
    private readonly HashSet<(int, int, StepKind)> _built = new();
    private readonly HashSet<BuildPoint> _planted = new();
    public StructurePlan? Blueprint { get; }
    public int VerifiedCompleted => Case.Family == "craft_chain" ? _bag.GetValueOrDefault(10) :
        Case.Family == "garden" ? _planted.Count : _built.Count;
    public int Total => Blueprint?.Steps.Count ?? Case.Count;
    public string LastAudit { get; private set; } = "未执行";

    public PairedRecoveryFixture(PairedRecoveryCase c)
    {
        Case = c; _bag = new(c.Bag); _chests = new(c.Chests); _chestObserved = c.ChestObserved;
        if (c.Family is "platform" or "build")
        {
            Blueprint = c.Family == "platform" ? new("original platform", "offline fixture", c.Count, 1,
                Enumerable.Range(0, c.Count).Select(x => new StructureStep(x, 0, StepKind.Block, 19, 0, 14))) :
                StructurePresets.NpcRooms(1, new(30, 1, 4, 11, 10, 12, 14, 13, 15, 10, 4, 3));
            foreach (var s in Blueprint.Steps.Take(c.Completed)) _built.Add((s.Dx, s.Dy, s.Kind));
        }
        if (c.Family == "garden")
            for (int i = 0; i < c.Completed; i++) _planted.Add(new(100 + i, 100));
        if (c.Family == "craft_chain") _bag[10] = c.Completed;
    }

    public Dictionary<int, int> Required() => Case.Family switch
    {
        "craft_chain" => new() { [1] = 4 * Math.Max(0, Case.Count - _bag.GetValueOrDefault(10)) },
        "garden" => new() { [16] = Case.Count - _planted.Count },
        _ => Blueprint!.Steps.Where(s => !_built.Contains((s.Dx, s.Dy, s.Kind))).GroupBy(s => s.Item)
            .ToDictionary(g => g.Key, g => g.Count())
    };
    private static Dictionary<string, int> Named(IReadOnlyDictionary<int, int> stock) => stock
        .Where(p => p.Value > 0).OrderBy(p => p.Key).ToDictionary(p => Names[p.Key], p => p.Value);
    public PairedObservation Observe() => new(Case.Family, Total, VerifiedCompleted, Case.SiteAccessible,
        Named(Required()), Named(_bag), _chestObserved ? Named(_chests) : null);
    public string Snapshot() => "离线现场实测（不是完整 Terraria 世界）：\n" +
        "工作台已在旁边；附近无可采集树木、矿物或额外种子；不购买、不作弊，不扩大目标。\n" +
        "RemainingMaterials 是原目标扣除已核实完成部分后的剩余材料清单，不是成功判定。\n" +
        "NearbyChests=null 表示箱内库存尚未展开，不表示空箱；可用 chests 补查。\n" +
        "SiteAccessible=false 表示原工作位置仍被玩家建筑挡住，不能拆除或改位置。\n" +
        JsonSerializer.Serialize(Observe(), RecoveryBenchmark.Json);
    public TaskFailureReceipt Receipt() => new("paired-original", Case.Family, "execution_interrupted",
        "上次执行中断；请依据最新现场和实际库存判断后续，不继承旧确认。",
        $"原目标共{Total}项，已核实{VerifiedCompleted}项；保留原坐标与已有成果。",
        Array.Empty<RecoveryStock>(), "库存摘要见最新现场；本回执未列物品，不代表空库存。", true);
    public string Signature() => JsonSerializer.Serialize(new { bag = _bag.OrderBy(x => x.Key),
        chests = _chests.OrderBy(x => x.Key), built = _built.OrderBy(x => x.Item1).ThenBy(x => x.Item2)
            .Select(x => new { x = x.Item1, y = x.Item2, kind = x.Item3 }),
        planted = _planted.OrderBy(x => x.X).ThenBy(x => x.Y) });
    public string Query(AgentCall call)
    {
        if (call.Tool == "chests") _chestObserved = true;
        return call.Tool switch
        {
            "inventory" => "最新主背包：" + JsonSerializer.Serialize(Named(_bag)),
            "chests" => "最新可用附近箱子（只查未取）：" + JsonSerializer.Serialize(Named(_chests)),
            "recipe" or "house_materials" => "离线原目标的剩余材料：" + JsonSerializer.Serialize(Named(Required())) +
                "；木椅每把4木材，在工作台制作；此处平台/建筑使用已备好的物品，种植消耗太阳花种子。",
            "look" => Snapshot(),
            "control" when call.Mode == "stop" => "已停止，未执行后续任务。",
            _ => "离线夹具没有此查询的新证据；没有改变库存或世界。"
        };
    }

    public bool Feasible() => Case.SiteAccessible && Required().All(p => Have(p.Key) >= p.Value);
    private int Have(int id) => _bag.GetValueOrDefault(id) + _chests.GetValueOrDefault(id);
    private bool Take(int id, int n)
    {
        if (n < 0 || Have(id) < n) return false;
        int bag = Math.Min(n, _bag.GetValueOrDefault(id)); _bag[id] = _bag.GetValueOrDefault(id) - bag;
        if (n > bag) _chests[id] = _chests.GetValueOrDefault(id) - (n - bag);
        return true;
    }
    private void Add(int id, int n) => _bag[id] = _bag.GetValueOrDefault(id) + n;

    // Only called after a new approval. All completion is audited by shared production jobs.
    public bool Execute(bool newApproval)
    {
        if (!newApproval || !Feasible()) { LastAudit = "未获新确认或现场/资源不足；没有执行。"; return false; }
        bool done;
        if (Case.Family == "craft_chain")
        {
            var plan = CraftChainPlanner.Build(new[] { new ChainTarget(10, Case.Count) },
                Enumerable.Range(1, Names.Length - 1).ToDictionary(i => i, Have), new[] { 100 }, new Catalog());
            var job = new CraftChainJob(plan);
            if (!job.Confirm(job.PreviewId, plan.Signature, DateTimeOffset.UtcNow)) return false;
            var site = new CraftSite(this);
            for (int i = 0; i < 100 && job.State == ChainState.Running; i++) job.Step(site);
            done = job.State == ChainState.Completed && _bag.GetValueOrDefault(10) >= Case.Count;
        }
        else if (Blueprint != null)
        {
            var site = new BuildSite(this); var job = new StructureBuildJob(Blueprint);
            for (int i = 0; i < Blueprint.Steps.Count + 2 && job.State == StructureJobState.Running; i++) job.Step(site);
            done = job.Verified(site) && job.Reused == Case.Completed && VerifiedCompleted == Total;
        }
        else
        {
            var cells = Enumerable.Range(0, Case.Count).Select(x => new GardenCell(new(100 + x, 100),
                GardenCrop.Daybloom, GardenGrowth.Empty, GardenBed.Planter, true, "offline fixed cell")).ToArray();
            var remaining = cells.Where(c => !_planted.Contains(c.At)).Select(c => (c.At, c.Crop)).ToHashSet();
            var plan = GardenPlan.BuildFixed(new(GardenOperation.Plant, GardenCrop.Daybloom, remaining.Count, Exact: true),
                new(100, 100), new Dictionary<GardenCrop, int> { [GardenCrop.Daybloom] = Have(16) }, cells, remaining);
            var job = new GardenJob(plan); var site = new GardenSite(this);
            if (!job.Confirm(job.PreviewId, plan.Signature, DateTimeOffset.UtcNow)) return false;
            for (int i = 0; i < Case.Count + 2 && job.State == GardenState.Running; i++) job.Tick(site);
            done = job.State == GardenState.Completed && _planted.Count == Case.Count;
        }
        LastAudit = done ? "共享生产规划器/执行器验收通过。" : "执行后未通过验收。";
        return done;
    }
    private sealed class Catalog : ICraftChainCatalog
    {
        public IEnumerable<ChainRecipe> Recipes(int item) => item == 10 ? new[] {
            new ChainRecipe(10, 10, 1, new[] { new IngredientRequirement(1, 4, new[] { 1 }) }, new[] { 100 }) } : Array.Empty<ChainRecipe>();
        public IEnumerable<int> StationItems(int tile) => Array.Empty<int>();
        public bool Collectable(int item) => false;
    }
    private sealed class CraftSite(PairedRecoveryFixture f) : ICraftChainSite
    {
        public bool Valid(out string reason) { reason = "原位置不变"; return f.Case.SiteAccessible; }
        public int Stock(int item) => f.Have(item);
        public ChainGatherResult Collect(int item, out string message) { message = "无可采集资源"; return ChainGatherResult.Failed; }
        public bool Craft(ChainStep s, out string message)
        {
            message = "实际扣料制作";
            if (s.Ingredients!.Any(p => f.Have(p.Key) < p.Value)) return false;
            foreach (var p in s.Ingredients!) f.Take(p.Key, p.Value);
            f.Add(s.Item, s.Count * s.Recipe!.Output); return true;
        }
        public bool Place(ChainStep s, out string message) { message = "此夹具无新制作站"; return false; }
        public bool Deliver(IReadOnlyList<ChainTarget> targets, out string message)
        {
            message = "成品交付";
            foreach (var t in targets) { int n = Math.Max(0, t.Count - f._bag.GetValueOrDefault(t.Item));
                if (!f.Take(t.Item, n)) return false; f.Add(t.Item, n); }
            return true;
        }
        public bool Audit(IReadOnlyList<ChainTarget> targets, out string message)
        { message = "检查实际成品"; return targets.All(t => f._bag.GetValueOrDefault(t.Item) >= t.Count); }
        public void StopGather() { }
    }
    private sealed class BuildSite(PairedRecoveryFixture f) : IStructureSite
    {
        public CellState Inspect(StructureStep s) => f._built.Contains((s.Dx, s.Dy, s.Kind)) ? CellState.Done :
            f.Case.SiteAccessible ? CellState.Empty : CellState.Blocked;
        public bool Clear(StructureStep s, out string error) { error = "禁止扩大范围"; return false; }
        public bool Place(StructureStep s, out string error)
        { error = "材料不足"; if (!f.Case.SiteAccessible || !f.Take(s.Item, 1)) return false;
            f._built.Add((s.Dx, s.Dy, s.Kind)); return true; }
    }
    private sealed class GardenSite(PairedRecoveryFixture f) : IGardenSite
    {
        public bool Valid(out string reason) { reason = "固定原种植位置"; return f.Case.SiteAccessible; }
        public GardenStep Work(GardenCell c, GardenOperation op, out GardenReceipt receipt, out string message)
        {
            receipt = new(0, 0, 0); message = "材料不足或重复目标";
            if (f._planted.Contains(c.At) || !f.Take(16, 1)) return GardenStep.Failed;
            f._planted.Add(c.At); receipt = new(1, 0, 0); return GardenStep.Done;
        }
        public bool Planted(GardenCell c) => f._planted.Contains(c.At);
        public bool Removed(GardenCell c) => false;
        public void Stop() { }
    }
}

public static class PairedRecoveryCases
{
    public static List<PairedRecoveryCase> Create()
    {
        var cases = new List<PairedRecoveryCase>();
        foreach (string family in new[] { "craft_chain", "platform", "build", "garden" })
        foreach (string mechanism in new[] { "stock", "chest", "progress", "site" })
        {
            var a = new PairedRecoveryCase { Family = family, Mechanism = mechanism, Count = family == "craft_chain" ? 2 : family == "garden" ? 4 : 6,
                Completed = family == "craft_chain" ? 1 : family == "garden" ? 1 : family == "platform" ? 2 : 35,
                Goal = family switch { "craft_chain" => "按原目标做共2把木椅，保留已经做出的木椅", "platform" => "在原位置铺共6格平台，保留已完成格子",
                    "build" => "按原蓝图在原位置盖1间NPC小屋，保留已完成格子", _ => "在原固定4个位置种共4株太阳花，保留已种植株" },
                PairId = family + "_" + mechanism, Variant = "a" };
            a.Id = a.PairId + "_a";
            a.Bag = new PairedRecoveryFixture(a).Required();
            int critical = mechanism == "progress" && family == "build" ?
                new PairedRecoveryFixture(a).Blueprint!.Steps[a.Completed - 1].Item : a.Bag.First(p => p.Value > 0).Key;
            if (mechanism == "chest") { a.ChestObserved = false; a.Chests[critical] = a.Bag[critical]; a.Bag.Remove(critical); }
            var b = JsonSerializer.Deserialize<PairedRecoveryCase>(JsonSerializer.Serialize(a))!;
            b.Id = b.PairId + "_b"; b.Variant = "b";
            if (mechanism == "stock") b.Bag[critical]--;
            if (mechanism == "chest") b.Chests[critical]--;
            if (mechanism == "progress") b.Completed--;
            if (mechanism == "site") b.SiteAccessible = false;
            cases.Add(a); cases.Add(b);
        }
        return cases;
    }

    // A deterministic, resource-aware rule using exactly the observation supplied to the models.
    // No Case, variant, expected label or private feasibility function is accessible here.
    public static AgentCall Rule(PairedObservation o)
    {
        if (!o.SiteAccessible) return new("ask", Text: "原位置仍受阻，请先处理现场；不会拆除或更换位置。");
        if (o.NearbyChests == null) return new("chests");
        if (o.RemainingMaterials.Any(p => o.Backpack.GetValueOrDefault(p.Key) + o.NearbyChests.GetValueOrDefault(p.Key) < p.Value))
            return new("ask", Text: "剩余材料不足，附近没有可采集资源；请补齐材料后再继续。");
        return new("replan");
    }
}
