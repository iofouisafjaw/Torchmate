using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TerrarAI.Content.Companion;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrarAI.Content.Systems;

// Other mods as tools, all by soft reference (reflection / Mod.Call): nothing here is required, and each
// bridge just reports "not installed" when its mod is missing or its internals changed.
internal static class ModTools
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    internal static string Coins(long copper)
    {
        if (copper <= 0) return "免费";
        var parts = new List<string>();
        foreach (var (unit, name) in new[] { (1000000L, "铂金"), (10000L, "金"), (100L, "银"), (1L, "铜") })
            if (copper >= unit) { parts.Add($"{copper / unit}{name}"); copper %= unit; }
        return string.Join(" ", parts);
    }

    private static string Summary(IEnumerable<Item> items, int max)
    {
        var list = items.Where(i => i != null && !i.IsAir).GroupBy(i => i.type)
            .Select(g => (name: Lang.GetItemNameValue(g.Key), count: g.Sum(i => i.stack)))
            .OrderByDescending(x => x.count).ToList();
        if (list.Count == 0) return "空";
        return string.Join("、", list.Take(max).Select(x => $"{x.name}×{x.count}")) + (list.Count > max ? $" 等 {list.Count} 种" : "");
    }

    // ---- Magic Storage: everything in the world's storage hearts, and taking materials out ----------
    internal static class Storage
    {
        private static Type? Heart => ModLoader.TryGetMod("MagicStorage", out var mod)
            ? mod.Code.GetType("MagicStorage.Components.TEStorageHeart") : null;
        internal static bool Available => Heart != null;

        private static List<TileEntity> Hearts()
        {
            var heart = Heart;
            return heart == null ? new() : TileEntity.ByID.Values.Where(heart.IsInstanceOfType).ToList();
        }

        internal static List<Item> Items()
        {
            var items = new List<Item>();
            foreach (var heart in Hearts())
            {
                try
                {
                    if (heart.GetType().GetMethod("GetStoredItems", Any, Type.EmptyTypes)?.Invoke(heart, null) is IEnumerable stored)
                        items.AddRange(stored.OfType<Item>());
                }
                catch (Exception ex) { ModContent.GetInstance<TerrarAI>().Logger.Warn("Magic Storage read failed: " + ex.Message); }
            }
            return items;
        }

        internal static int Count(int type) => Items().Where(i => i.type == type).Sum(i => i.stack);

        internal static string Describe(string filter)
        {
            if (!Available) return "没装魔法存储。";
            if (Hearts().Count == 0) return "这个世界还没有放存储核心。";
            var items = Items();
            if (filter.Length > 0)
            {
                var hits = items.Where(i => Lang.GetItemNameValue(i.type).Contains(filter)).ToList();
                return hits.Count == 0 ? $"魔法存储里没有「{filter}」。" : "魔法存储里：" + Summary(hits, 20);
            }
            return "魔法存储里：" + Summary(items, 30);
        }

        // Take `amount` of `type` out of storage into the player's inventory; returns how many arrived.
        internal static int Take(Player player, int type, int amount)
        {
            int got = 0;
            foreach (var heart in Hearts())
            {
                if (got >= amount) break;
                var lookFor = new Item(type) { stack = amount - got };
                Item? withdrawn = null;
                try
                {
                    var methods = heart.GetType().GetMethods(Any);
                    var plain = methods.FirstOrDefault(m => m.Name == "Withdraw" && m.GetParameters() is var p &&
                        p.Length == 2 && p[0].ParameterType == typeof(Item) && p[1].ParameterType == typeof(bool));
                    var tryAny = methods.FirstOrDefault(m => m.Name == "TryWithdraw" && m.GetParameters() is var p &&
                        p.Length == 3 && p[0].ParameterType == typeof(Item) && p[2].ParameterType == typeof(bool));
                    withdrawn = plain != null ? plain.Invoke(heart, new object[] { lookFor, false }) as Item
                        : tryAny?.Invoke(heart, new object[] { lookFor, false, false }) as Item;
                }
                catch (Exception ex) { ModContent.GetInstance<TerrarAI>().Logger.Warn("Magic Storage withdraw failed: " + ex.Message); }
                if (withdrawn == null || withdrawn.IsAir) continue;
                got += withdrawn.stack;
                Give(player, withdrawn);
            }
            return got;
        }
    }

    // Straight into the inventory (so a craft in the same tick can use it); whatever does not fit drops at the feet.
    internal static void Give(Player player, Item item)
    {
        var left = player.GetItem(player.whoAmI, item, GetItemSettings.LootAllSettings);
        if (!left.IsAir) player.QuickSpawnItem(player.GetSource_Misc("TerrarAI"), left.type, left.stack);
    }

    // ---- 更好的体验 (ImproveGame): big backpack and auto-fishers ------------------------------------------
    internal static class ImproveGame
    {
        private static Mod? Mod => ModLoader.TryGetMod("ImproveGame", out var mod) ? mod : null;
        internal static bool Available => Mod != null;

        internal static List<Item> BigBag(Player player)
        {
            try { return Mod?.Call("GetBigBagItems", player) is IEnumerable items ? items.OfType<Item>().ToList() : new(); }
            catch { return new(); }
        }

        internal static List<Item> Fishers()
        {
            var items = new List<Item>();
            foreach (var te in TileEntity.ByID.Values.Where(t => t.GetType().FullName == "ImproveGame.Content.Tiles.TEAutofisher"))
                if (te.GetType().GetField("fish", Any)?.GetValue(te) is Item[] fish) items.AddRange(fish.Where(i => i != null));
            return items;
        }

        internal static string Describe(Player player)
        {
            if (!Available) return "";
            var bag = BigBag(player);
            var fish = Fishers();
            var lines = new List<string>();
            if (bag.Any(i => !i.IsAir)) lines.Add("大背包里：" + Summary(bag, 20));
            if (fish.Any(i => !i.IsAir)) lines.Add("自动钓鱼机里：" + Summary(fish, 15));
            return string.Join("\n", lines);
        }
    }

    // ---- Census: which town NPCs are still missing and what each one needs ---------------------------
    internal static class Census
    {
        internal static string Describe()
        {
            var present = Main.npc.Where(n => n.active && n.townNPC).Select(n => n.TypeName).Distinct().ToList();
            string have = "已经住进来的：" + (present.Count > 0 ? string.Join("、", present) : "还没有");
            if (!ModLoader.TryGetMod("Census", out var mod)) return have + "\n（没装 Census，查不到其他 NPC 的入住条件）";
            try
            {
                var system = mod.Code.GetType("Census.CensusSystem");
                var instance = system?.GetField("instance", Any)?.GetValue(null);
                if (system?.GetField("realTownNPCsInfos", Any)?.GetValue(instance) is not IEnumerable infos) return have;
                var missing = new List<string>();
                foreach (var info in infos)
                {
                    int type = (int)(info.GetType().GetField("type", Any)?.GetValue(info) ?? 0);
                    if (type <= 0 || NPC.AnyNPCs(type)) continue;
                    var condition = info.GetType().GetField("conditions", Any)?.GetValue(info) as Terraria.Localization.LocalizedText;
                    missing.Add($"{Lang.GetNPCNameValue(type)}：{(condition == null ? "条件未知" : ChatTags.Strip(condition.Value, ItemName))}");
                }
                return have + (missing.Count > 0 ? "\n还没来的和条件：\n" + string.Join("\n", missing.Take(25)) : "\n能来的都来了。");
            }
            catch (Exception ex) { return have + "\n（Census 读取失败：" + ex.Message + "）"; }
        }
    }

    // "SilverCoin" / "Fargowiltas/OverloadWorm" / "73" → the item's display name, or "" when unknown.
    internal static string ItemName(string key)
    {
        try
        {
            if (int.TryParse(key, out int id)) return id > 0 && id < ItemLoader.ItemCount ? Lang.GetItemNameValue(id) : "";
            if (ItemID.Search.TryGetId(key, out id)) return Lang.GetItemNameValue(id);
            int slash = key.IndexOf('/');
            if (slash > 0 && ModContent.TryFind<ModItem>(key[..slash], key[(slash + 1)..], out var modItem))
                return Lang.GetItemNameValue(modItem.Type);
        }
        catch { }
        return "";
    }

    // ---- shops: every town NPC's shop, vanilla or modded (Fargo's Mutant, AlchemistNPC ...) -------------
    internal sealed record Offer(int Npc, Item Item, long Price, bool Unlocked, bool Here);

    internal static IEnumerable<Offer> Sellers(int itemType)
    {
        foreach (var shop in NPCShopDatabase.AllShops.OfType<NPCShop>())
        {
            NPCShop.Entry? entry = null;
            try { entry = shop.Entries.FirstOrDefault(e => !e.Disabled && e.Item?.type == itemType); }
            catch { }
            if (entry == null) continue;
            bool unlocked;
            try { unlocked = entry.ConditionsMet(); } catch { unlocked = false; }
            long price = entry.Item.shopCustomPrice ?? entry.Item.value;
            yield return new Offer(shop.NpcType, entry.Item, price, unlocked, NPC.AnyNPCs(shop.NpcType));
        }
    }

    internal static string Shop(int itemType)
    {
        var offers = Sellers(itemType).ToList();
        string name = Lang.GetItemNameValue(itemType);
        if (offers.Count > 0) return Describe(offers);
        // Mods often sell their own version (Fargo's Mutant sells 腐烂诱饵 instead of 蠕虫诱饵): same effect text.
        var alike = Equivalents(itemType).ToList();
        return alike.Count == 0 ? $"没有 NPC 卖{name}。"
            : $"没有 NPC 卖{name}，但有效果一样的替代品：\n" + Describe(alike);
    }

    private static string Describe(IEnumerable<Offer> offers) => string.Join("\n", offers.Take(5).Select(o =>
        $"{Lang.GetNPCNameValue(o.Npc)} 卖{Lang.GetItemNameValue(o.Item.type)}，{Coins(o.Price)}；" +
        (!o.Unlocked ? "现在还没解锁（进度或条件不够，比如还没打过对应的 Boss）" : o.Here ? "现在就能买" : "条件够了，但这个 NPC 还没住进来")));

    private static string Tooltip(int type)
    {
        try
        {
            var tip = Lang.GetTooltip(type);
            return string.Join(" ", Enumerable.Range(0, tip.Lines).Select(tip.GetLine)).Trim();
        }
        catch { return ""; }
    }

    // Shop items whose tooltip contains this item's tooltip: another mod's version of the same summon or tool.
    internal static IEnumerable<Offer> Equivalents(int itemType)
    {
        string tip = Tooltip(itemType);
        if (tip.Length < 4) yield break;
        var seen = new HashSet<int> { itemType };
        foreach (var shop in NPCShopDatabase.AllShops.OfType<NPCShop>())
        {
            List<NPCShop.Entry> entries;
            try { entries = shop.Entries.Where(e => !e.Disabled && e.Item != null && !e.Item.IsAir).ToList(); }
            catch { continue; }
            foreach (var entry in entries)
            {
                if (!seen.Add(entry.Item.type) || !Tooltip(entry.Item.type).Contains(tip)) continue;
                bool unlocked;
                try { unlocked = entry.ConditionsMet(); } catch { unlocked = false; }
                yield return new Offer(shop.NpcType, entry.Item, entry.Item.shopCustomPrice ?? entry.Item.value, unlocked, NPC.AnyNPCs(shop.NpcType));
            }
        }
    }

    // Buy from a town NPC that lives in this world, with the player's own coins.
    internal static (bool Done, string Message) Buy(Player player, int itemType, int count)
    {
        string name = Lang.GetItemNameValue(itemType);
        var offer = Sellers(itemType).Where(o => o.Unlocked && o.Here).OrderBy(o => o.Price).FirstOrDefault();
        if (offer == null) return (false, Shop(itemType) + "\n所以现在买不到。");
        count = Math.Clamp(count, 1, Math.Max(1, offer.Item.maxStack));
        long total = offer.Price * count;
        if (total > int.MaxValue || !player.BuyItem((int)total)) return (false, $"钱不够：{count} 个{name}要 {Coins(total)}。");
        Give(player, new Item(itemType) { stack = count });
        return (true, $"从{Lang.GetNPCNameValue(offer.Npc)}那买了 {name} ×{count}，花了 {Coins(total)}。");
    }
}
