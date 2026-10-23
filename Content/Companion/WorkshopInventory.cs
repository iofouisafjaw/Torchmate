using System;
using System.Collections.Generic;
using System.Linq;

namespace TerrarAI.Content.Companion;

public readonly record struct WorkshopStack(int Type, int Count);

// Shared by platform and housing crafting. A complete transaction is computed
// before the Terraria adapter commits any inventory slot.
public static class WorkshopInventory
{
    public static WorkshopStack[]? Craft(IReadOnlyList<WorkshopStack> source,
        IReadOnlyDictionary<int, int> cost, int outputType, int output, int maxStack, out string failure)
    {
        failure = "";
        if (outputType < 1 || output < 1 || maxStack < 1 || cost.Count == 0 || cost.Any(p => p.Key < 1 || p.Value < 1))
        { failure = "配方无效"; return null; }
        var slots = source.ToArray();
        foreach (var (type, count) in cost)
        {
            if (slots.Where(i => i.Type == type).Sum(i => i.Count) < count)
            { failure = "材料不足"; return null; }
            int left = count;
            for (int i = 0; i < slots.Length && left > 0; i++)
            {
                if (slots[i].Type != type) continue;
                int take = Math.Min(left, slots[i].Count), remain = slots[i].Count - take; left -= take;
                slots[i] = remain == 0 ? default : new(type, remain);
            }
        }
        int pending = output;
        // Reuse matching stacks before taking empty slots, preserving other items.
        foreach (int i in Enumerable.Range(0, slots.Length).OrderBy(i => slots[i].Count > 0 ? 0 : 1))
        {
            var slot = slots[i]; if (slot.Count > 0 && slot.Type != outputType) continue;
            int add = Math.Min(pending, Math.Max(0, maxStack - slot.Count));
            if (add == 0) continue;
            slots[i] = new(outputType, slot.Count + add); pending -= add;
            if (pending == 0) return slots;
        }
        failure = "主背包放不下合成产物"; return null;
    }
}
