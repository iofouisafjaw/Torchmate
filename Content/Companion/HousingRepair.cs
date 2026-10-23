using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TerrarAI.Content.Companion;

// Inspection is always read-only. Even an explicit repair request opens a preview.
public static class HousingRequest
{
    public static bool Matches(string text) => text.Length <= 240
        && Regex.IsMatch(text, "房|屋|住处|housing", RegexOptions.IgnoreCase)
        && Regex.IsMatch(text, "检查|检测|能不能住|不能住|不住|不合格|修补|修好|修理|补齐|修复|修一下|修房|修屋|check|repair", RegexOptions.IgnoreCase)
        && !Regex.IsMatch(text, "以后|记住|不要检查|别检查|不要修|别修|取消|怎么修|如何修");
}

public sealed record RepairRecipe(int Item, int Output, IReadOnlyDictionary<int, int> Ingredients, bool Workbench);
public sealed record RepairCraft(RepairRecipe Recipe, int Batches);

// Budget the actual shortfall only, retaining batch excess. No inventory mutations here.
public sealed class HousingMaterialBudget
{
    public List<RepairCraft> Crafts { get; } = new();
    public Dictionary<int, int> Missing { get; } = new();
    public Dictionary<int, int> Consumed { get; } = new();
    public bool CanPrepare => Missing.Count == 0;

    public static HousingMaterialBudget Calculate(IReadOnlyDictionary<int, int> required,
        Func<int, int> have, Func<int, RepairRecipe?> recipe)
    {
        var result = new HousingMaterialBudget();
        var stock = new Dictionary<int, int>();
        int Stock(int item) { if (!stock.ContainsKey(item)) stock[item] = have(item); return stock[item]; }
        // Native caller orders the portable workbench before furniture that needs it.
        foreach (var (item, quantity) in required)
        {
            int shortfall = Math.Max(0, quantity - Stock(item));
            if (shortfall > 0)
            {
                var r = recipe(item);
                if (r == null || r.Output < 1 || r.Ingredients.Count == 0 || r.Ingredients.Any(p => p.Value < 1))
                    result.Missing[item] = result.Missing.GetValueOrDefault(item) + shortfall;
                else
                {
                    int batches = (shortfall + r.Output - 1) / r.Output;
                    foreach (var (raw, count) in r.Ingredients)
                    {
                        int cost = checked(count * batches), available = Stock(raw);
                        int lacking = Math.Max(0, cost - available);
                        if (lacking > 0) result.Missing[raw] = result.Missing.GetValueOrDefault(raw) + lacking;
                        stock[raw] = Math.Max(0, available - cost);
                        result.Consumed[raw] = result.Consumed.GetValueOrDefault(raw) + cost;
                    }
                    stock[item] = Stock(item) + batches * r.Output;
                    result.Crafts.Add(new(r, batches));
                }
            }
            stock[item] = Math.Max(0, Stock(item) - quantity);
        }
        return result;
    }
}

// Repairs only fill empty cells. This adapter reuses the normal structure job without
// allowing its terrain-clearing behavior, including when the world changes mid-build.
public sealed class NonDestructiveStructureSite(IStructureSite inner) : IStructureSite
{
    public CellState Inspect(StructureStep step) => inner.Inspect(step) switch
    { CellState.Clearable => CellState.Blocked, var state => state };
    public bool Clear(StructureStep step, out string error) { error = "修补不会拆除现有方块。"; return false; }
    public bool Place(StructureStep step, out string error)
    {
        if (Inspect(step) != CellState.Empty) { error = "修补位置已变化，保留现场。"; return false; }
        return inner.Place(step, out error);
    }
}

public static class HousingRepairLayout
{
    public static StructureStep Furniture(StructurePlan palette,int tile)=>
        palette.Steps.Single(s=>s.Kind==StepKind.Furniture&&s.Type==tile);
    // Coordinates are relative to the room bounds, as in the existing house presets.
    // Every footprint cell must be empty/safe; every bottom column needs real support.
    public static StructureStep? Floor(int width, int height, StructureStep template,
        Func<int, int, bool> empty, Func<int, int, bool> support, ISet<(int X, int Y)> reserved,
        Func<int, int, double> rank, Func<int, int, bool>? originAllowed = null)
    {
        var origins = from x in Enumerable.Range(1, Math.Max(0, width - 2))
                      from y in Enumerable.Range(1, Math.Max(0, height - 2))
                      orderby rank(x, y)
                      select (x, y);
        foreach (var (x, y) in origins)
        {
            if (originAllowed?.Invoke(x, y) == false) continue;
            var step = template with { Dx = x, Dy = y }; var cells = step.Footprint().ToArray();
            if (cells.Any(p => p.X <= 0 || p.X >= width - 1 || p.Y <= 0 || p.Y >= height - 1 || reserved.Contains(p) || !empty(p.X, p.Y))) continue;
            int bottom = cells.Max(p => p.Y);
            if (cells.Where(p => p.Y == bottom).Any(p => !support(p.X, p.Y + 1))) continue;
            return step;
        }
        return null;
    }
}
