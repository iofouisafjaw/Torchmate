using System;
using System.Collections.Generic;
using System.Linq;
using TerrarAI.Content.Companion;
using Terraria;
using Terraria.ID;

namespace TerrarAI.Content.Systems;

// Same live recipe lookup as normal crafting, restricted to ordinary wood/gel and
// a workbench. No storage withdrawal or dropped outputs inside a repair transaction.
internal static class RepairCrafting
{
    internal static Recipe? Native(int item) => Crafting.RecipesFor(item).FirstOrDefault(r =>
        r.Mod == null && r.createItem.stack > 0 && r.Conditions.Count == 0 &&
        r.requiredTile.All(t => t < 0 || t == TileID.WorkBenches) &&
        r.requiredItem.Any(i => !i.IsAir) &&
        r.requiredItem.Where(i => !i.IsAir).All(i => i.type is ItemID.Wood or ItemID.Gel && i.stack > 0));

    internal static RepairRecipe? RecipeFor(int item)
    {
        var r = Native(item);
        return r == null ? null : new(item, r.createItem.stack, r.requiredItem.Where(i => !i.IsAir)
            .GroupBy(i => i.type).ToDictionary(g => g.Key, g => g.Sum(i => i.stack)), r.requiredTile.Contains(TileID.WorkBenches));
    }

    internal static HousingMaterialBudget Budget(Player p, StructurePlan plan)
    {
        // A newly placed workbench must be available before any other furniture is crafted.
        var needs = plan.Materials().OrderBy(x => x.Key == ItemID.WorkBench ? 0 : 1).ToDictionary(x => x.Key, x => x.Value);
        return HousingMaterialBudget.Calculate(needs, i => HousingInspector.Count(p, i), RecipeFor);
    }

    internal static bool Make(Player p, RepairCraft craft, out string error)
    {
        var r = Native(craft.Recipe.Item);
        var current = RecipeFor(craft.Recipe.Item);
        if (r == null || current == null || current.Output != craft.Recipe.Output || current.Workbench != craft.Recipe.Workbench ||
            current.Ingredients.Count != craft.Recipe.Ingredients.Count || current.Ingredients.Any(x => craft.Recipe.Ingredients.GetValueOrDefault(x.Key) != x.Value))
        { error = "配方已变化，停止修补，请重新检测。"; return false; }
        p.AdjTiles();
        if (r.requiredTile.Any(t => t >= 0 && !p.adjTile[t]))
        { error = "角色不在工作台旁，修补已停止；请靠近工作台后重新检测。"; return false; }
        return Crafting.MakeExact(p, r, craft.Batches, out error);
    }
}
