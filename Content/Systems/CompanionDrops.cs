// TerrarAI replacement implementation, 2026-10-20. See docs/source-replacement.md.
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
namespace TerrarAI.Content.Systems;
internal static class CompanionDrops
{
    internal static void Pickup(Player player, NPC agent)
    {
        // Reuse the recipe gatherer's native GetItem remainder handling.
        var types = Main.item.Where(item => item != null && item.active && !item.IsAir &&
            Vector2.Distance(item.Center, agent.Center) <= 64).Select(item => item.type).Distinct().ToArray();
        foreach (int type in types) PickupMatching(player, agent, type, 64);
    }
    // Moved from ProgramGathering, shared by recipes and the NPC adapter.
    // The native API returns leftovers; they must remain active in the world.
    internal static void PickupMatching(Player player, NPC agent, int type, float radius = 96)
    {
        if (!ServerAuthority.IsServer || !player.active || player.dead) return;
        for (int index = 0; index < Main.maxItems; index++)
        {
            var drop = Main.item[index];
            if (drop == null || !drop.active || drop.IsAir || drop.type != type || Vector2.Distance(drop.Center, agent.Center) > radius) continue;
            if (!player.inventory.Take(50).Any(slot => slot.IsAir || slot.type == type && slot.stack < slot.maxStack)) continue;
            var position = drop.position;
            var remaining = player.GetItem(player.whoAmI, drop, GetItemSettings.InventoryEntityToPlayerInventorySettings);
            Main.item[index] = remaining;
            remaining.position = position;
            remaining.active = !remaining.IsAir && remaining.stack > 0;
            if (Main.netMode == NetmodeID.Server) NetMessage.SendData(MessageID.SyncItem, number: index);
        }
    }
}
