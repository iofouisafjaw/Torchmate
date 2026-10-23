using System;
using System.Collections.Generic;
using System.Linq;

namespace TerrarAI.Content.Companion;

public readonly record struct BuildPoint(int X, int Y);
public readonly record struct BuildRegion(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left + 1;
    public int Height => Bottom - Top + 1;
    public static BuildRegion Between(BuildPoint a, BuildPoint b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
}
public enum PlatformCell { Empty, Platform, Protected }
public sealed record PlatformSlot(BuildPoint At, PlatformCell Before);
public sealed record PlatformAudit(int Required, int Present, int Missing, int Blocked)
{
    public bool Passed => Required > 0 && Present == Required;
}

// A bounded, immutable specification; coordinates are never supplied by a language model.
public sealed class PlatformBlueprint
{
    public const int MaxWidth = 160, MaxHeight = 96, MaxLayers = 6;
    public BuildRegion Region { get; }
    public int Layers { get; }
    public int Spacing { get; }
    public IReadOnlyList<PlatformSlot> Slots { get; }
    public int Needed => Slots.Count(s => s.Before == PlatformCell.Empty);
    public int Reused => Slots.Count(s => s.Before == PlatformCell.Platform);
    public int Conflicts => Slots.Count(s => s.Before == PlatformCell.Protected);
    public PlatformBlueprint(BuildRegion region, int layers, int spacing, Func<BuildPoint, PlatformCell> inspect)
    {
        if (region.Width < 4 || region.Width > MaxWidth || region.Height < 1 || region.Height > MaxHeight)
            throw new ArgumentException($"范围需宽 4–{MaxWidth} 格、高 1–{MaxHeight} 格。");
        if (layers is < 1 or > MaxLayers || spacing is < 4 or > 12)
            throw new ArgumentException("层数需 1–6，层间距需 4–12 格。");
        if ((layers - 1) * spacing >= region.Height)
            throw new ArgumentException($"{layers} 层、间距 {spacing} 格至少需要 {(layers - 1) * spacing + 1} 格高度。");
        Region = region; Layers = layers; Spacing = spacing;
        var slots = new List<PlatformSlot>();
        for (int layer = 0; layer < layers; layer++)
            for (int x = region.Left; x <= region.Right; x++)
            {
                var point = new BuildPoint(x, region.Bottom - layer * spacing);
                slots.Add(new(point, inspect(point)));
            }
        Slots = slots.AsReadOnly();
    }
    public PlatformAudit Audit(Func<BuildPoint, PlatformCell> inspect)
    {
        int present = 0, blocked = 0;
        foreach (var s in Slots)
        {
            var state = inspect(s.At);
            if (state == PlatformCell.Platform) present++;
            else if (state == PlatformCell.Protected) blocked++;
        }
        return new(Slots.Count, present, Slots.Count - present, blocked);
    }
}

public interface IPlatformSite
{
    PlatformCell Inspect(BuildPoint point);
    int Available { get; }
    // Consume one item only if a previously empty cell receives a tile; the job verifies its platform shape.
    bool TryPlace(BuildPoint point, out string error);
}

public enum PlatformJobState { Running, Completed, Cancelled, Failed }
public sealed class PlatformBuildJob
{
    public PlatformBlueprint Blueprint { get; }
    public PlatformJobState State { get; private set; } = PlatformJobState.Running;
    public int Cursor { get; private set; }
    public int Placed { get; private set; }
    public int Reused { get; private set; }
    public string Reason { get; private set; } = "";
    public PlatformAudit? Audit { get; private set; }
    public PlatformBuildJob(PlatformBlueprint blueprint)
    {
        if (blueprint.Conflicts > 0) throw new ArgumentException("蓝图与已有地形或建筑冲突，请重新框选。");
        Blueprint = blueprint;
    }
    public void Cancel(string reason)
    { if (State == PlatformJobState.Running) { State = PlatformJobState.Cancelled; Reason = reason; } }
    public void Step(IPlatformSite site)
    {
        if (State != PlatformJobState.Running) return;
        if (Cursor < Blueprint.Slots.Count)
        {
            var point = Blueprint.Slots[Cursor].At;
            switch (site.Inspect(point))
            {
                case PlatformCell.Protected: Fail($"tile({point.X},{point.Y}) 出现障碍，已停止且未拆除。"); return;
                case PlatformCell.Platform: Reused++; break;
                default:
                    if (site.Available < 1) { Fail("背包木平台不足，已停止。"); return; }
                    if (!site.TryPlace(point, out string error)) { Fail(error); return; }
                    Placed++;
                    if (site.Inspect(point) != PlatformCell.Platform)
                    { Cursor++; Fail(error.Length > 0 ? error : "新放置的方块未通过平台检查，已停止；保留消耗记录。"); return; }
                    break;
            }
            Cursor++;
        }
        if (Cursor == Blueprint.Slots.Count)
        {
            Audit = Blueprint.Audit(site.Inspect);
            State = Audit.Passed ? PlatformJobState.Completed : PlatformJobState.Failed;
            Reason = Audit.Passed ? "所有层均与确认蓝图一致。" : $"最终核验发现 {Audit.Missing} 格未达标。";
        }
    }
    private void Fail(string error) { Reason = error; State = PlatformJobState.Failed; }
}
