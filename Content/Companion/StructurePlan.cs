using System;
using System.Collections.Generic;
using System.Linq;

namespace TerrarAI.Content.Companion;

// A building blueprint, independent of Terraria so ordering, materials and progress can be tested.
// Coordinates are offsets from the blueprint's top-left corner; a language model never supplies them.

public enum StepKind { Block, Wall, Furniture }

// Block shape as Terraria numbers it: 0 solid, 1 half block, 2..5 slopes.
// For furniture, (Dx, Dy) is the object's placement origin; it covers Width x Height tiles starting
// OriginX/OriginY tiles left/up of that origin.
public sealed record StructureStep(int Dx, int Dy, StepKind Kind, int Type, int Style, int Item,
    byte Shape = 0, byte Paint = 0, int Width = 1, int Height = 1, int Direction = 0, int OriginX = 0, int OriginY = 0)
{
    public IEnumerable<(int X, int Y)> Footprint()
    {
        for (int x = 0; x < Width; x++)
            for (int y = 0; y < Height; y++) yield return (Dx - OriginX + x, Dy - OriginY + y);
    }
}

public enum CellState
{
    Done,       // already exactly what the blueprint wants: reused, costs nothing
    Empty,      // free: will be built
    Clearable,  // natural terrain / plants in the way: dug out first (drops go to the ground)
    Blocked     // something the player made, or unbreakable: never removed
}

public sealed class StructurePlan
{
    public const int MaxWidth = 120, MaxHeight = 80;
    public string Name { get; }
    public string Source { get; }
    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<StructureStep> Steps { get; }
    public IReadOnlyList<string> Skipped { get; }

    public StructurePlan(string name, string source, int width, int height, IEnumerable<StructureStep> steps,
        IEnumerable<string>? skipped = null)
    {
        if (width < 1 || height < 1 || width > MaxWidth || height > MaxHeight)
            throw new ArgumentException($"蓝图尺寸需在 {MaxWidth}×{MaxHeight} 格以内。");
        Name = name; Source = source; Width = width; Height = height;
        Steps = Order(steps).ToList().AsReadOnly();
        Skipped = (skipped ?? Array.Empty<string>()).ToList().AsReadOnly();
    }

    // Build order: solid blocks bottom-up (so everything has support), then background walls,
    // then furniture bottom-up (it needs floors and walls to hang on).
    private static IEnumerable<StructureStep> Order(IEnumerable<StructureStep> steps) => steps
        .OrderBy(s => s.Kind switch { StepKind.Block => 0, StepKind.Wall => 1, _ => 2 })
        .ThenByDescending(s => s.Dy).ThenBy(s => s.Dx);

    // Item type -> count, for everything the blueprint places.
    public Dictionary<int, int> Materials()
    {
        var need = new Dictionary<int, int>();
        foreach (var s in Steps.Where(s => s.Item > 0))
            need[s.Item] = need.GetValueOrDefault(s.Item) + 1;
        return need;
    }

    // Materials still to buy/craft, given what is already in place and what the player carries.
    public Dictionary<int, int> Missing(IReadOnlyList<CellState> states, Func<int, int> have)
    {
        var need = new Dictionary<int, int>();
        for (int i = 0; i < Steps.Count; i++)
            if (states[i] != CellState.Done && Steps[i].Item > 0)
                need[Steps[i].Item] = need.GetValueOrDefault(Steps[i].Item) + 1;
        return need.Select(p => (p.Key, lack: p.Value - have(p.Key))).Where(p => p.lack > 0)
            .ToDictionary(p => p.Key, p => p.lack);
    }
}

public interface IStructureSite
{
    CellState Inspect(StructureStep step);
    bool Clear(StructureStep step, out string error);
    bool Place(StructureStep step, out string error);
}

public enum StructureJobState { Running, Completed, Cancelled, Failed }

// One step per call: clear what is in the way, then place. Stops (never forces) on anything unexpected.
public sealed class StructureBuildJob
{
    public bool Verified(IStructureSite site)=>State==StructureJobState.Completed&&Plan.Steps.All(s=>site.Inspect(s)==CellState.Done);
    public StructurePlan Plan { get; }
    public StructureJobState State { get; private set; } = StructureJobState.Running;
    public int Cursor { get; private set; }
    public int Placed { get; private set; }
    public int Reused { get; private set; }
    public int Cleared { get; private set; }
    public int Failed { get; private set; }
    public string Reason { get; private set; } = "";
    private readonly List<string> _problems = new();
    public IReadOnlyList<string> Problems => _problems;

    public StructureBuildJob(StructurePlan plan) => Plan = plan;

    public void Cancel(string reason)
    { if (State == StructureJobState.Running) { State = StructureJobState.Cancelled; Reason = reason; } }

    public void Step(IStructureSite site)
    {
        if (State != StructureJobState.Running) return;
        if (Cursor >= Plan.Steps.Count) { Finish(); return; }
        var step = Plan.Steps[Cursor];
        var state = site.Inspect(step);
        if (state == CellState.Done) { Reused++; Cursor++; return; }
        if (state == CellState.Blocked)
        { Note(step, "被已有的东西挡住，跳过"); Cursor++; return; }
        if (state == CellState.Clearable)
        {
            if (!site.Clear(step, out string clearError)) { Note(step, clearError); Cursor++; return; }
            Cleared++;
        }
        if (site.Place(step, out string error)) Placed++;
        else
        {
            Note(step, error);
            if (error.StartsWith("材料不足")) { State = StructureJobState.Failed; Reason = error; return; }
        }
        Cursor++;
        if (Cursor >= Plan.Steps.Count) Finish();
    }

    private void Note(StructureStep step, string error)
    {
        Failed++;
        if (_problems.Count < 8) _problems.Add($"({step.Dx},{step.Dy}) {error}");
    }

    private void Finish()
    {
        State = StructureJobState.Completed;
        Reason = Failed == 0 ? "按蓝图建好了。" : $"建好了，有 {Failed} 格没能照蓝图放（见下方）。";
    }
}

// Ready-made blueprints that need no file: generated from a few parameters.
public static class StructurePresets
{
    // Item / tile ids are passed in so this stays testable without Terraria.
    public sealed record Palette(int BlockTile, int BlockItem, int WallType, int WallItem,
        int DoorTile, int DoorItem, int TableTile, int TableItem, int ChairTile, int ChairItem, int TorchTile, int TorchItem);

    // A row of NPC rooms sharing walls: each room 8 wide x 4 tall inside (10x6 outside),
    // with a door on its left wall, a table, a chair and a torch, which meets the housing rules.
    public static StructurePlan NpcRooms(int rooms, Palette p)
    {
        rooms = Math.Clamp(rooms, 1, 4);
        int width = 1 + rooms * 9, height = 6;
        var steps = new List<StructureStep>();
        for (int x = 0; x < width; x++)
        {
            steps.Add(new(x, 0, StepKind.Block, p.BlockTile, 0, p.BlockItem));
            steps.Add(new(x, height - 1, StepKind.Block, p.BlockTile, 0, p.BlockItem));
        }
        for (int r = 0; r <= rooms; r++)
        {
            int x = r * 9;
            bool door = r < rooms;          // the rightmost wall is solid
            steps.Add(new(x, 1, StepKind.Block, p.BlockTile, 0, p.BlockItem));
            if (!door)
                for (int y = 2; y <= 4; y++) steps.Add(new(x, y, StepKind.Block, p.BlockTile, 0, p.BlockItem));
            else
                steps.Add(new(x, 3, StepKind.Furniture, p.DoorTile, 0, p.DoorItem, Width: 1, Height: 3, OriginY: 1));
        }
        for (int r = 0; r < rooms; r++)
        {
            int left = r * 9 + 1;
            for (int x = left; x < left + 8; x++)
                for (int y = 1; y <= 4; y++) steps.Add(new(x, y, StepKind.Wall, p.WallType, 0, p.WallItem));
            // Row 4 is the one just above the floor; chair and table origins sit on their bottom row.
            steps.Add(new(left + 2, 4, StepKind.Furniture, p.ChairTile, 0, p.ChairItem, Width: 1, Height: 2, Direction: 1, OriginY: 1));
            steps.Add(new(left + 4, 4, StepKind.Furniture, p.TableTile, 0, p.TableItem, Width: 3, Height: 2, OriginX: 1, OriginY: 1));
            steps.Add(new(left + 6, 2, StepKind.Furniture, p.TorchTile, 0, p.TorchItem));
        }
        string name = rooms == 1 ? "NPC 小屋（木）" : $"NPC 联排 ×{rooms}（木）";
        return new StructurePlan(name, "预设", width, height, steps);
    }
}
