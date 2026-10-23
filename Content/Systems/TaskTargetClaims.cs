// TerrarAI replacement implementation, 2026-10-20. See docs/source-replacement.md.
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria.ModLoader;
namespace TerrarAI.Content.Systems;
// Local task leases; there is no separate multi-agent scheduler.
public sealed class TaskTargetClaims : ModSystem
{
    private static readonly Dictionary<Point, int> Trees = new();
    internal static bool Occupied(Point point) => Trees.ContainsKey(point);
    internal static bool Acquire(Point point, int actor)
    {
        if (Trees.TryGetValue(point, out int owner)) return owner == actor;
        Trees.Add(point, actor); return true;
    }
    internal static void Release(int actor)
    { foreach (var point in Trees.Where(entry => entry.Value == actor).Select(entry => entry.Key).ToArray()) Trees.Remove(point); }
    public override void OnWorldUnload() => Trees.Clear();
    public override void OnWorldLoad() => Trees.Clear();
    public override void Unload() => Trees.Clear();
}
