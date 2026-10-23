using System;
using System.Collections.Generic;
using System.Linq;

namespace TerrarAI.Content.Companion;

public static class ArenaRequest
{
    public static bool Matches(string text)
    {
        string s = text.Trim().Replace(" ", "");
        if (AgentProtocol.LooksLikeQuestion(s) || System.Text.RegularExpressions.Regex.IsMatch(s,"不要|不用|取消|停止|别|以后|下次|如果|不想|别再")) return false;
        return s.Length <= 160 && (s.Contains("克眼") || s.Contains("克苏鲁之眼") || s.Contains("EyeofCthulhu", StringComparison.OrdinalIgnoreCase))
            && (s.Contains("准备") || s.Contains("备战") || s.Contains("竞技场"));
    }
}

public sealed record ArenaPreviewStamp(string Stock, string Recipes, string Stations, string Scene, string Blocked)
{
    public bool Matches(ArenaPreviewStamp live, DateTimeOffset expires, DateTimeOffset now) => this == live && now < expires;
}

public static class ArenaBuffFrames
{
    // SceneMetrics' native active ranges; style and full object frames must also match.
    public static bool Campfire(int frameY) => frameY is >= 0 and < 36;
    public static bool HeartLantern(int frameY) => frameY is >= 324 and <= 358;
}

// Native item metadata supplies furniture dimensions and recipes; this geometry is
// shared by previews, material planning, execution and offline tests.
public sealed class ArenaLayout
{
    public const int Width = 100, Height = 16, Layers = 3, Spacing = 6;
    public StructurePlan Ghost { get; }
    public StructurePlan Fixtures { get; }
    public IReadOnlyList<ChainTarget> Potions { get; }
    public int PlatformItem { get; }
    public ArenaLayout(int platformItem, int platformTile, int woodItem, int woodTile,
        StructureStep campfire, StructureStep lantern, IReadOnlyList<ChainTarget> potions)
    {
        if (campfire.Width != 3 || campfire.Height != 2 || lantern.Width != 1 || lantern.Height != 2
            || campfire.Kind != StepKind.Furniture || lantern.Kind != StepKind.Furniture
            || potions.Count is not (0 or 4) || potions.Any(t => t.Count < 1))
            throw new ArgumentException("家具规格或药水目标已变化，不能使用此竞技场布局。");
        PlatformItem = platformItem; Potions = potions.ToArray();
        var fixtures = new List<StructureStep>();
        foreach (int center in new[] { 25, 75 })
        {
            for (int x = center - 1; x <= center + 1; x++)
                fixtures.Add(new(x, 15, StepKind.Block, woodTile, 0, woodItem));
            fixtures.Add(campfire with { Dx = center - 1 + campfire.OriginX, Dy = 13 + campfire.OriginY });
            fixtures.Add(new(center, 7, StepKind.Block, woodTile, 0, woodItem));
            fixtures.Add(lantern with { Dx = center + lantern.OriginX, Dy = 8 + lantern.OriginY });
        }
        Fixtures = new("克眼竞技场设施", "程序布局", Width, Height, fixtures);
        var platforms = Enumerable.Range(0, Layers).SelectMany(row => Enumerable.Range(0, Width)
            .Select(x => new StructureStep(x, row * Spacing, StepKind.Block, platformTile, 0, platformItem)));
        Ghost = new("克眼战前准备", "程序布局", Width, Height, platforms.Concat(fixtures));
        if (Ghost.Steps.SelectMany(s => s.Footprint()).GroupBy(p => p).Any(g => g.Count() > 1))
            throw new ArgumentException("竞技场蓝图存在重叠。");
    }
    public PlatformBlueprint Platforms(BuildPoint origin, Func<BuildPoint, PlatformCell> inspect) =>
        new(new(origin.X, origin.Y, origin.X + Width - 1, origin.Y + (Layers - 1) * Spacing), Layers, Spacing, inspect);
    public IReadOnlyList<ChainTarget> Targets(PlatformBlueprint platforms, IReadOnlyList<CellState> fixtures)
    {
        if (fixtures.Count != Fixtures.Steps.Count || platforms.Conflicts > 0 || fixtures.Any(s => s is CellState.Blocked or CellState.Clearable))
            throw new ArgumentException("竞技场有障碍，先换位置或手动处理后刷新。");
        var needs = Fixtures.Steps.Where((s, i) => fixtures[i] != CellState.Done).GroupBy(s => s.Item)
            .Select(g => new ChainTarget(g.Key, g.Count())).ToList();
        if (platforms.Needed > 0) needs.Insert(0, new(PlatformItem, platforms.Needed));
        return needs.Concat(Potions).GroupBy(t => t.Item).Select(g => new ChainTarget(g.Key, g.Sum(t => t.Count))).ToArray();
    }
}

public enum ArenaState { Preview, Supplying, Platforms, Fixtures, Completed, Failed, Cancelled }
public interface IArenaSite
{
    ICraftChainSite Supply { get; }
    IPlatformSite Platforms { get; }
    IStructureSite Fixtures { get; }
    bool Audit(out string reason);
}

public sealed class ArenaPreparationJob
{
    public CraftChainJob Supply { get; }
    public PlatformBuildJob Platforms { get; }
    public StructureBuildJob Fixtures { get; }
    public ArenaState State { get; private set; } = ArenaState.Preview;
    public string Reason { get; private set; } = "等待确认竞技场、材料和药水计划。";
    public Guid PreviewId => Supply.PreviewId;
    public ArenaPreparationJob(CraftChainPlan plan, PlatformBlueprint platforms, StructurePlan fixtures, DateTimeOffset? now = null)
    { Supply = new(plan, now); Platforms = new(platforms); Fixtures = new(fixtures); }
    public bool Confirm(Guid id, string signature, DateTimeOffset now)
    {
        if (State != ArenaState.Preview || !Supply.Confirm(id, signature, now)) return false;
        State = ArenaState.Supplying; return true;
    }
    public void Step(IArenaSite site, bool paused = false)
    {
        if (paused || State is ArenaState.Preview or ArenaState.Completed or ArenaState.Failed or ArenaState.Cancelled) return;
        if (!site.Supply.Valid(out string invalid)) { Fail(site, invalid); return; }
        switch (State)
        {
            case ArenaState.Supplying:
                Supply.Step(site.Supply); Reason = Supply.Reason;
                if (Supply.State == ChainState.Failed) Fail(site, Reason);
                else if (Supply.State == ChainState.Completed) State = ArenaState.Platforms;
                break;
            case ArenaState.Platforms:
                Platforms.Step(site.Platforms); Reason = Platforms.Reason;
                if (Platforms.State == PlatformJobState.Failed) Fail(site, Reason);
                else if (Platforms.State == PlatformJobState.Completed) State = ArenaState.Fixtures;
                break;
            case ArenaState.Fixtures:
                Fixtures.Step(site.Fixtures); Reason = Fixtures.Reason;
                if (Fixtures.Failed > 0 || Fixtures.State == StructureJobState.Failed) { Fail(site, "设施放置未通过：" + string.Join("；", Fixtures.Problems)); break; }
                if (Fixtures.State != StructureJobState.Completed) break;
                bool passed = site.Audit(out string audit); Reason = audit;
                if (!passed) Fail(site, audit); else State = ArenaState.Completed;
                break;
        }
    }
    public void Cancel(IArenaSite? site, string reason)
    {
        if (State is ArenaState.Completed or ArenaState.Failed or ArenaState.Cancelled) return;
        Supply.Cancel(site?.Supply, reason); site?.Supply.StopGather(); Platforms.Cancel(reason); Fixtures.Cancel(reason);
        State = ArenaState.Cancelled; Reason = reason;
    }
    private void Fail(IArenaSite site, string reason)
    { site.Supply.StopGather(); State = ArenaState.Failed; Reason = reason; }
}
