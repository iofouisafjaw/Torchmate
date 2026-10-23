using System;
using System.Collections.Generic;
using System.Linq;

namespace TerrarAI.Content.Companion;

public sealed record IngredientRequirement(int Type, int Count, IReadOnlyCollection<int> Accepted);
public sealed record IngredientAllocation(IReadOnlyDictionary<int, int> Used, IReadOnlyDictionary<int, int> Missing)
{
    // Bipartite flow reserves shared recipe-group items once and can reassign a
    // flexible ingredient when another ingredient needs its exact item type.
    public static IngredientAllocation Plan(IReadOnlyDictionary<int, int> stock, IReadOnlyList<IngredientRequirement> requirements)
    {
        if (requirements.Any(r => r.Count < 1 || r.Type < 1)) throw new ArgumentException("Invalid ingredient demand.");
        var types = stock.Where(p => p.Key > 0 && p.Value > 0).Select(p => p.Key).ToArray();
        int sink = types.Length + requirements.Count + 1;
        var graph = Enumerable.Range(0, sink + 1).Select(_ => new List<Edge>()).ToArray();
        Edge Add(int a, int b, int capacity)
        {
            var forward = new Edge(b, graph[b].Count, capacity); var back = new Edge(a, graph[a].Count, 0);
            graph[a].Add(forward); graph[b].Add(back); return forward;
        }
        var supply = new Dictionary<int, Edge>();
        for (int t = 0; t < types.Length; t++)
        {
            int type = types[t]; supply[type] = Add(0, t + 1, stock[type]);
            foreach (int r in Enumerable.Range(0, requirements.Count).OrderBy(r => requirements[r].Type == type ? 0 : 1))
                if (requirements[r].Type == type || requirements[r].Accepted.Contains(type))
                    Add(t + 1, types.Length + r + 1, requirements[r].Count);
        }
        var demand = requirements.Select((r, i) => Add(types.Length + i + 1, sink, r.Count)).ToArray();
        while (true)
        {
            var parent = Enumerable.Repeat(-1, graph.Length).ToArray(); var via = new int[graph.Length];
            var queue = new Queue<int>(); queue.Enqueue(0); parent[0] = 0;
            while (queue.Count > 0 && parent[sink] < 0)
            {
                int u = queue.Dequeue();
                for (int e = 0; e < graph[u].Count; e++)
                {
                    var edge = graph[u][e]; if (edge.Capacity <= 0 || parent[edge.To] >= 0) continue;
                    parent[edge.To] = u; via[edge.To] = e; queue.Enqueue(edge.To);
                }
            }
            if (parent[sink] < 0) break;
            int amount = int.MaxValue;
            for (int v = sink; v != 0; v = parent[v]) amount = Math.Min(amount, graph[parent[v]][via[v]].Capacity);
            for (int v = sink; v != 0; v = parent[v])
            {
                var edge = graph[parent[v]][via[v]]; edge.Capacity -= amount; graph[v][edge.Reverse].Capacity += amount;
            }
        }
        var used = supply.Where(p => stock[p.Key] > p.Value.Capacity).ToDictionary(p => p.Key, p => stock[p.Key] - p.Value.Capacity);
        var missing = new Dictionary<int, int>();
        for (int i = 0; i < requirements.Count; i++) if (demand[i].Capacity > 0)
                missing[requirements[i].Type] = missing.GetValueOrDefault(requirements[i].Type) + demand[i].Capacity;
        return new(used, missing);
    }
    private sealed class Edge(int to, int reverse, int capacity)
    { public int To = to, Reverse = reverse, Capacity = capacity; }
}
