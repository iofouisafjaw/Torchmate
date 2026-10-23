// TerrarAI replacement implementation, 2026-10-20. See docs/source-replacement.md.
using System;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
namespace TerrarAI.Content.Systems;
public enum ToolType { Pickaxe, Axe, Hammer, Weapon }
public static class ToolSelector
{
    private static int Power(Item item, ToolType kind) => kind switch
    { ToolType.Axe => item.axe, ToolType.Hammer => item.hammer, ToolType.Weapon => item.damage, _ => item.pick };
    internal static Item? FindInventoryTool(Player? player, ToolType toolType, int minPower = 0) => player?.inventory
        .Where(item => item != null && !item.IsAir && Power(item, toolType) > 0 && Power(item, toolType) >= minPower)
        .OrderByDescending(item => Power(item, toolType)).FirstOrDefault();
    public static Item? FindBestTool(Player? player, ToolType toolType, int minPower = 0)
    {
        if (!TerrarAI_Config.Get().EnableCreativeMode) return FindInventoryTool(player, toolType, minPower);
        var tool = new Item();
        tool.SetDefaults(toolType switch { ToolType.Axe => ItemID.PickaxeAxe, ToolType.Hammer => ItemID.TheBreaker,
            ToolType.Weapon => ItemID.TerraBlade, _ => ItemID.PickaxeAxe });
        switch (toolType)
        { case ToolType.Pickaxe: tool.pick = 1000; break; case ToolType.Axe: tool.axe = 500; break;
          case ToolType.Hammer: tool.hammer = 500; break; case ToolType.Weapon: tool.damage = 999; break; }
        return tool;
    }
    // Game IDs and threshold data; no upstream selector implementation retained.
    public static int GetTileStrength(int tileType) => tileType switch
    {
        TileID.LunarOre => 225, TileID.Chlorophyte => 200,
        TileID.Adamantite or TileID.Titanium => 150, TileID.Mythril or TileID.Orichalcum => 110,
        TileID.Cobalt or TileID.Palladium => 100,
        TileID.Ebonstone or TileID.Crimstone or TileID.Hellstone or TileID.Obsidian => 65,
        TileID.Demonite or TileID.Crimtane => 55, TileID.Meteorite => 50,
        TileID.Gold or TileID.Platinum => 45, TileID.Silver or TileID.Tungsten => 40,
        TileID.Copper or TileID.Tin or TileID.Iron or TileID.Lead => 35,
        _ => Math.Max(0, TileLoader.GetTile(tileType)?.MinPick ?? 0)
    };
    public static bool CanMineTile(Item? tool, int tileType) => tool is { IsAir: false, pick: > 0 } &&
        tool.pick >= GetTileStrength(tileType);
    internal static Item? FindCombatWeapon(Player player) => player.inventory.Take(50)
        .Where(item => !item.IsAir && item.damage > 0 && item.ammo == 0 && !item.consumable && !item.accessory &&
            item.pick == 0 && item.axe == 0 && item.hammer == 0 && !item.channel && !item.noUseGraphic &&
            !item.noMelee && item.CountsAsClass(DamageClass.Melee))
        .OrderByDescending(item => item.damage).ThenBy(item => item.useTime).FirstOrDefault();
}
