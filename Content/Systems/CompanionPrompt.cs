using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.Xna.Framework;
using TerrarAI.Content.Companion;
using Terraria;
using Terraria.ID;

namespace TerrarAI.Content.Systems;

internal static class CompanionPrompt
{
    private static readonly JsonSerializerOptions PromptJson = new()
    { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
    internal static string Build(NPC agent, Player? player, string command, string? result, Point? lockedTarget)
    {
        var position = new Point((int)(agent.Center.X / 16), (int)(agent.Center.Y / 16));
        var ores = new Dictionary<int, (int x, int y, int distance)>();
        for (int y = position.Y - 24; y <= position.Y + 24; y++)
            for (int x = position.X - 24; x <= position.X + 24; x++)
            {
                if (x < 2 || y < 2 || x >= Main.maxTilesX - 2 || y >= Main.maxTilesY - 2) continue;
                var tile = Framing.GetTileSafely(x, y);
                if (!tile.HasTile || !TileID.Sets.Ore[tile.TileType]) continue;
                int distance = System.Math.Abs(x - position.X) + System.Math.Abs(y - position.Y);
                if (!ores.TryGetValue(tile.TileType, out var old) || distance < old.distance)
                    ores[tile.TileType] = (x, y, distance);
            }
        var pick = ToolSelector.FindBestTool(player, ToolType.Pickaxe);
        var axe = ToolSelector.FindBestTool(player, ToolType.Axe);
        var state = new
        {
            task = command,
            at = new[] { position.X, position.Y },
            playerAt = player == null ? null : new[] { (int)(player.Center.X / 16), (int)(player.Center.Y / 16) },
            playerHp = player == null ? null : new[] { player.statLife, player.statLifeMax2 },
            day = Main.dayTime,
            time = Main.dayTime ? "day" : "night",
            pickaxe = pick?.pick ?? 0, axe = axe?.axe ?? 0,
            inventory = player?.inventory.Where(i => i != null && !i.IsAir && i.pick == 0 && i.axe == 0 && i.damage == 0)
                .Take(6).Select(i => new { name = i.Name, count = i.stack }).ToArray(),
            trees = TreeHelper.FindTreeBasesNear(position, 24).OrderBy(t =>
                System.Math.Abs(t.X - position.X) + System.Math.Abs(t.Y - position.Y)).Take(3).Select(t => new[] { t.X, t.Y }).ToArray(),
            ores = ores.OrderBy(p => p.Value.distance).Take(3).Select(p => new
                { name = TileID.Search.GetName(p.Key), at = new[] { p.Value.x, p.Value.y } }).ToArray(),
            lockedTarget = lockedTarget.HasValue ? new[] { lockedTarget.Value.X, lockedTarget.Value.Y } : null,
            result = CompanionProtocol.Clip(result, 160),
            rules = CompanionMemorySystem.Store?.PromptRules(),
            memory = CompanionMemorySystem.Store?.PromptMemory(command, includeRules: false) ?? ""
        };
        return JsonSerializer.Serialize(state, PromptJson);
    }
}
