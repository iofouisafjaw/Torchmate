using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerrarAI.Content.Systems;

internal static class CompanionObservation
{
    // Logging snapshot, deliberately separate from the smaller model prompt.
    internal static object Capture(Player? player, NPC? agent = null, Point? target = null, int radius = 0) => new
    {
        day = Main.dayTime,
        time = Main.dayTime ? "day" : "night",
        pickaxe = ToolSelector.FindBestTool(player, ToolType.Pickaxe)?.pick ?? 0,
        axe = ToolSelector.FindBestTool(player, ToolType.Axe)?.axe ?? 0,
        targetTiles = target.HasValue ? Enumerable.Range(-radius, radius * 2 + 1)
            .SelectMany(dy => Enumerable.Range(-radius, radius * 2 + 1).Select(dx => new
            { x = target.Value.X + dx, y = target.Value.Y + dy,
                type = Framing.GetTileSafely(target.Value.X + dx, target.Value.Y + dy).HasTile
                    ? (int?)Framing.GetTileSafely(target.Value.X + dx, target.Value.Y + dy).TileType : null })).ToArray() : null,
        playerAt = player == null ? null : new[] { (int)(player.Center.X / 16), (int)(player.Center.Y / 16) },
        agentAt = agent == null ? null : new[] { (int)(agent.Center.X / 16), (int)(agent.Center.Y / 16) },
        inventory = player?.inventory.Where(item => item != null && !item.IsAir)
            .GroupBy(item => item.type).Select(group => new { type = group.Key, count = group.Sum(item => item.stack) }).OrderBy(item => item.type).ToArray(),
        ruleRevision = CompanionMemorySystem.Store?.Memory.RuleRevision
    };
}
