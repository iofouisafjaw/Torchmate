using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerrarAI.Content.Companion;

namespace TerrarAI.Content.Systems;

internal sealed record InventoryEntry(int Type, string Name, int Count);
internal sealed record WorldInfo(string WorldId, string Name, string Size, string Difficulty,
    string Stage, string Evil, string Clock, string Weather, string Biome, string Layer,
    int Life, int LifeMax, int Mana, int ManaMax, int TileX, int TileY,
    string[] Defeated, InventoryEntry[] Inventory, string[] Equipment, string[] Nearby,
    int NearbyHostiles, string Coverage);

internal static class CompanionWorldInfo
{
    // Small game-thread snapshot for passive reminders; no NPC/world scans or
    // full inventory grouping needed. Coins/ammo and extra storage are excluded.
    internal static ReminderWorldState ForReminders(Player player)=>new(Main.dayTime,Main.bloodMoon,player.statLife,player.statLifeMax2,
        ReminderInventory.FreeSlots(player.inventory,item=>item==null||item.IsAir));
    // Called only on the game thread. No world files or unexplored tile arrays are scanned.
    internal static WorldInfo Capture(Player player)
    {
        var defeated = new List<string>();
        void Boss(bool done, string name) { if (done) defeated.Add(name); }
        Boss(NPC.downedSlimeKing, "史莱姆王"); Boss(NPC.downedBoss1, "克苏鲁之眼");
        Boss(NPC.downedBoss2, WorldGen.crimson ? "克苏鲁之脑" : "世界吞噬怪"); Boss(NPC.downedQueenBee, "蜂王");
        Boss(NPC.downedDeerclops, "独眼巨鹿");
        Boss(NPC.downedBoss3, "骷髅王"); Boss(Main.hardMode, "血肉墙");
        Boss(NPC.downedQueenSlime, "史莱姆皇后"); Boss(NPC.downedMechBoss1, "毁灭者");
        Boss(NPC.downedMechBoss2, "双子魔眼"); Boss(NPC.downedMechBoss3, "机械骷髅王");
        Boss(NPC.downedPlantBoss, "世纪之花"); Boss(NPC.downedGolemBoss, "石巨人");
        Boss(NPC.downedFishron, "猪龙鱼公爵"); Boss(NPC.downedEmpressOfLight, "光之女皇");
        Boss(NPC.downedAncientCultist, "拜月教邪教徒"); Boss(NPC.downedMoonlord, "月亮领主");
        string biome = player.ZoneDungeon ? "地牢" : player.ZoneLihzhardTemple ? "丛林神庙"
            : player.ZoneUnderworldHeight ? "地狱" : player.ZoneSkyHeight ? "太空"
            : player.ZoneBeach ? "海洋" : player.ZoneGlowshroom ? "发光蘑菇地"
            : player.ZoneJungle ? "丛林" : player.ZoneSnow ? "雪原"
            : player.ZoneDesert ? "沙漠" : "森林／普通地形";
        if (player.ZoneCorrupt) biome += " · 腐化";
        if (player.ZoneCrimson) biome += " · 猩红";
        if (player.ZoneHallow) biome += " · 神圣";
        string layer = player.ZoneUnderworldHeight ? "地狱" : player.ZoneRockLayerHeight ? "洞穴"
            : player.ZoneDirtLayerHeight ? "地下" : player.ZoneSkyHeight ? "太空" : "地表";
        int minutes = ((int)(Main.time / 60) + (Main.dayTime ? 270 : 1170)) % 1440;
        string clock = $"{(Main.dayTime ? "白天" : "夜里")} {minutes / 60:00}:{minutes % 60:00}";
        string weather = Main.bloodMoon ? "血月" : Main.eclipse ? "日食" : Main.raining ? "下雨" : "晴朗";
        if (Main.invasionType > 0) weather += " · 入侵事件进行中";
        var inventory = player.inventory.Take(58).Where(i => i != null && !i.IsAir)
            .GroupBy(i => i.type).Select(g => new InventoryEntry(g.Key, g.First().Name, g.Sum(i => i.stack))).ToArray();
        var nearby = Main.npc.Where(n => n?.active == true && n.friendly &&
                Microsoft.Xna.Framework.Vector2.DistanceSquared(n.Center, player.Center) <= 960 * 960)
            .Select(n => n.FullName).Distinct().Take(8).ToArray();
        int hostiles = Main.npc.Count(n => n?.active == true && !n.friendly && !n.townNPC && n.damage > 0 &&
            Microsoft.Xna.Framework.Vector2.DistanceSquared(n.Center, player.Center) <= 960 * 960);
        return new WorldInfo(CompanionMemorySystem.Store?.Memory.WorldId ?? "", Main.worldName,
            Main.maxTilesX <= 4200 ? "小型" : Main.maxTilesX <= 6400 ? "中型" : "大型",
            Main.GameMode switch { 1 => "专家", 2 => "大师", 3 => "旅行", _ => "经典" },
            Main.hardMode ? "困难模式" : "困难模式之前", WorldGen.crimson ? "猩红" : "腐化",
            clock, weather, biome, layer, player.statLife, player.statLifeMax2, player.statMana, player.statManaMax2,
            (int)(player.Center.X / 16), (int)(player.Center.Y / 16), defeated.ToArray(), inventory,
            player.armor.Take(10).Where(i => i != null && !i.IsAir).Select(i => i.Name).ToArray(),
            nearby, hostiles, "当前已加载世界的原版进度、主背包与装备、玩家环境、60格内NPC；不含箱子、未探索地形、模组专属进度。");
    }

    // Everything the brain's rule engine needs, as plain data (game thread only). Stable keys are
    // used for bosses and items so the rules do not depend on the display language.
    internal static object ForBrain(Player player, WorldInfo world)
    {
        var bosses = new List<string>();
        void B(bool done, string key) { if (done) bosses.Add(key); }
        B(NPC.downedSlimeKing, "king_slime"); B(NPC.downedBoss1, "eye_of_cthulhu"); B(NPC.downedBoss2, "evil_boss");
        B(NPC.downedQueenBee, "queen_bee"); B(NPC.downedBoss3, "skeletron"); B(NPC.downedDeerclops, "deerclops");
        B(Main.hardMode, "wall_of_flesh"); B(NPC.downedQueenSlime, "queen_slime"); B(NPC.downedMechBoss1, "the_destroyer");
        B(NPC.downedMechBoss2, "the_twins"); B(NPC.downedMechBoss3, "skeletron_prime"); B(NPC.downedPlantBoss, "plantera");
        B(NPC.downedGolemBoss, "golem"); B(NPC.downedFishron, "duke_fishron"); B(NPC.downedEmpressOfLight, "empress_of_light");
        B(NPC.downedAncientCultist, "lunatic_cultist"); B(NPC.downedMoonlord, "moon_lord");
        var events = new List<string>();
        void E(bool done, string key) { if (done) events.Add(key); }
        E(NPC.downedGoblins, "goblin_army"); E(NPC.downedPirates, "pirate_invasion"); E(NPC.downedFrost, "frost_legion");
        E(NPC.downedMartians, "martian_madness"); E(NPC.downedHalloweenKing, "pumpkin_moon"); E(NPC.downedChristmasIceQueen, "frost_moon");
        var town = Main.npc.Where(n => n != null && n.active && n.townNPC && n.ModNPC is not NPCs.AIAgentNPC)
            .Select(n => n.TypeName).Distinct().ToArray();

        static string Key(Item i) => i.type < ItemID.Count ? ItemID.Search.GetName(i.type) : i.ModItem?.FullName ?? i.type.ToString();
        static string Class(Item i) => i.CountsAsClass(DamageClass.Summon) || i.CountsAsClass(DamageClass.SummonMeleeSpeed) ? "summon"
            : i.CountsAsClass(DamageClass.Magic) ? "magic" : i.CountsAsClass(DamageClass.Ranged) ? "ranged" : "melee";
        var carried = player.inventory.Take(58).Where(i => i != null && !i.IsAir).ToArray();
        var inventory = carried.GroupBy(i => i.type).Select(g => new
        {
            key = Key(g.First()), name = g.First().Name, count = g.Sum(i => i.stack), material = g.First().material
        }).ToArray();
        var weapons = carried.Where(i => i.damage > 0 && !i.accessory && i.ammo == 0 && i.pick == 0 && i.axe == 0 && i.hammer == 0)
            .GroupBy(i => i.type).Select(g => g.First())
            .Select(i => new { key = Key(i), name = i.Name, damage = i.damage, @class = Class(i), useTime = i.useTime,
                dps = Math.Round(i.damage * 60.0 / Math.Max(1, i.useTime), 1) })
            .OrderByDescending(w => w.dps).Take(5).ToArray();
        var pick = carried.Where(i => i.pick > 0).OrderByDescending(i => i.pick).FirstOrDefault();
        var axe = carried.Where(i => i.axe > 0).OrderByDescending(i => i.axe).FirstOrDefault();
        var hammer = carried.Where(i => i.hammer > 0).OrderByDescending(i => i.hammer).FirstOrDefault();
        var accessories = player.armor.Skip(3).Take(7).Where(i => i != null && !i.IsAir).ToArray();
        var hookItem = player.miscEquips[4];
        bool hook = hookItem != null && !hookItem.IsAir || carried.Any(i => i.shoot > 0 && i.shoot < Main.projHook.Length && Main.projHook[i.shoot]);
        var heal = carried.Where(i => i.potion && i.healLife > 0).GroupBy(i => i.type)
            .Select(g => new { name = g.First().Name, count = g.Sum(i => i.stack), heal = g.First().healLife })
            .OrderByDescending(h => h.count).FirstOrDefault();
        long coins = carried.Sum(i => i.type switch
        {
            ItemID.CopperCoin => (long)i.stack, ItemID.SilverCoin => i.stack * 100L,
            ItemID.GoldCoin => i.stack * 10000L, ItemID.PlatinumCoin => i.stack * 1000000L, _ => 0L
        });
        var seeds = new List<string>();
        if (Main.drunkWorld) seeds.Add("drunk"); if (Main.getGoodWorld) seeds.Add("for_the_worthy");
        if (Main.tenthAnniversaryWorld) seeds.Add("celebrationmk10"); if (Main.dontStarveWorld) seeds.Add("the_constant");
        if (Main.notTheBeesWorld) seeds.Add("not_the_bees"); if (Main.remixWorld) seeds.Add("dont_dig_up");
        if (Main.noTrapsWorld) seeds.Add("no_traps"); if (Main.zenithWorld) seeds.Add("zenith");
        string[] clock = world.Clock.Split(' ');
        return new
        {
            world = new
            {
                id = world.WorldId, name = world.Name, size = world.Size, difficulty = world.Difficulty,
                hardmode = Main.hardMode, evil = WorldGen.crimson ? "crimson" : "corruption", day = Main.dayTime,
                clock = clock.Length > 1 ? clock[1] : world.Clock, bloodMoon = Main.bloodMoon, eclipse = Main.eclipse,
                raining = Main.raining, moonPhase = Main.moonPhase, invasion = Main.invasionType > 0, seeds
            },
            progress = new
            {
                bosses, events, townNpcs = town, altarsSmashed = WorldGen.altarCount,
                mods = ModLoader.Mods.Select(m => m.Name).Where(n => n is not ("ModLoader" or "TerrarAI")).ToArray(),
                checklist = ModIntegration.ChecklistForBrain()   // null unless Boss Checklist is enabled
            },
            gameData = ModIntegration.GameDataPath,
            player = new
            {
                life = player.statLife, lifeMax = player.statLifeMax2, mana = player.statMana, manaMax = player.statManaMax2,
                defense = (int)player.statDefense, biome = world.Biome, layer = world.Layer, x = world.TileX, y = world.TileY,
                pick = pick?.pick ?? 0, pickName = pick?.Name, axe = (axe?.axe ?? 0) * 5, axeName = axe?.Name,
                hammer = hammer?.hammer ?? 0, hook, wings = player.wingTimeMax > 0 || accessories.Any(i => i.wingSlot > 0),
                boots = accessories.Any(i => i.shoeSlot > 0 && i.Name.Contains("靴")) || player.accRunSpeed > 4f,
                weapons, armor = player.armor.Take(3).Where(i => i != null && !i.IsAir).Select(i => i.Name).ToArray(),
                accessories = accessories.Select(i => i.Name).ToArray(),
                healing = heal == null ? null : new { heal.name, heal.count, heal.heal }, coins, inventory,
                hostilesNearby = world.NearbyHostiles
            },
            nearby = world.Nearby
        };
    }

    // Keep the 4B context bounded; the full inventory remains available on the world page.
    internal static object ForPrompt(WorldInfo world, bool compact = false) => new
    {
        world.Name, world.Difficulty, world.Stage, world.Clock, world.Weather, world.Biome, world.Layer,
        life = new { current = world.Life, maximum = world.LifeMax },
        mana = new { current = world.Mana, maximum = world.ManaMax }, world.Defeated,
        inventory = world.Inventory.Take(compact ? 6 : 16).Select(i => new { i.Name, i.Count }),
        inventoryOmitted = Math.Max(0, world.Inventory.Length - (compact ? 6 : 16)), Equipment = world.Equipment.Take(compact ? 3 : 10), Nearby = world.Nearby.Take(compact ? 3 : 8), world.NearbyHostiles
    };
}
