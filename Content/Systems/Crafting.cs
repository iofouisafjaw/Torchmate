using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TerrarAI.Content.Companion;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrarAI.Content.Systems;

// The companion crafts with the game's own recipes and the player's own materials: the same items
// get used up as if the player crafted by hand, and nothing is created from nothing.
internal static class Crafting
{
    private static Dictionary<string, int>? _byName;
    private static List<string>? _namesLongestFirst;

    private static void Index()
    {
        if (_byName != null) return;
        _byName = new Dictionary<string, int>();
        for (int i = 0; i < Recipe.numRecipes; i++)
        {
            var r = Main.recipe[i];
            if (r?.createItem == null || r.createItem.IsAir || r.Disabled) continue;
            string name = Lang.GetItemNameValue(r.createItem.type);
            if (name.Length >= 2) _byName.TryAdd(name, r.createItem.type);
        }
        _namesLongestFirst = _byName.Keys.OrderByDescending(n => n.Length).ToList();
    }

    internal static void Reset() { _byName = null; _namesLongestFirst = null; _allNames = null; }

    internal static string Name(int type) => Lang.GetItemNameValue(type);

    // ---- what the player said ------------------------------------------------------------------
    private static readonly Regex Verb = new("做|合成|造|搓|建|打造|制作");
    private static readonly Regex Question = new("怎么|如何|吗|？|\\?|什么|啥|需要|还差|多少|能不能|可以吗");
    private static readonly Regex Count = new(@"(\d+|[一两二三四五六七八九十]+)\s*(个|把|块|张|件|只|根|组|支|瓶|套)");

    // "建一个工作台" / "帮我做 10 个火把" -> (item, how many). Questions ("工作台怎么做") are not orders.
    internal static (int Type, int Count)? ParseOrder(string text)
    {
        if (!Verb.IsMatch(text) || Question.IsMatch(text)) return null;
        Index();
        string? name = _namesLongestFirst!.FirstOrDefault(text.Contains);
        if (name == null) return null;
        var m = Count.Match(text);
        int count = m.Success ? Number(m.Groups[1].Value) : 1;
        return (_byName![name], Math.Clamp(count, 1, 999));
    }

    private static int Number(string s)
    {
        if (int.TryParse(s, out int n)) return n;
        const string digits = "零一二三四五六七八九";
        if (s == "两") return 2;
        if (s.StartsWith("十")) return 10 + (s.Length > 1 ? digits.IndexOf(s[1]) : 0);
        if (s.Contains('十')) return digits.IndexOf(s[0]) * 10 + (s.EndsWith("十") ? 0 : digits.IndexOf(s[^1]));
        return Math.Max(1, digits.IndexOf(s[0]));
    }

    // A name the model wrote ("工作台", "木墙") -> item type: exact item name first, then the closest craftable name.
    internal static int Resolve(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return 0;
        Index();
        if (_byName!.TryGetValue(name, out int crafted)) return crafted;
        foreach (var (type, item) in ContentSamples.ItemsByType)
            if (!item.IsAir && Name(type) == name) return type;
        string? near = _namesLongestFirst!.FirstOrDefault(n => n.Contains(name)) ?? _namesLongestFirst!.FirstOrDefault(name.Contains);
        return near != null ? _byName[near] : 0;
    }

    // Transfers must not choose a similar item and move the wrong stack.
    internal static int ResolveExact(string name)
    {
        name = name.Trim();
        if (name == "木头") return ItemID.Wood;
        var exact = ContentSamples.ItemsByType.Where(p => !p.Value.IsAir && Name(p.Key).Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Key).Distinct().ToArray();
        if (exact.Length == 1) return exact[0];
        if (exact.Length > 1) return 0;
        if (ItemID.Search.TryGetId(name, out int id) && ContentSamples.ItemsByType.ContainsKey(id)) return id;
        return ModContent.TryFind<Terraria.ModLoader.ModItem>(name, out var modItem) ? modItem.Type : 0;
    }

    private static List<(string Name, int Type)>? _allNames;

    // Real item names that share the most characters with what the model wrote ("神圣之刃" → 圣剑、…).
    internal static List<string> Similar(string name, int max = 8)
    {
        name = name.Trim();
        if (name.Length == 0) return new();
        _allNames ??= ContentSamples.ItemsByType.Where(p => !p.Value.IsAir).Select(p => (Name(p.Key), p.Key))
            .Where(x => x.Item1.Length >= 2).GroupBy(x => x.Item1).Select(g => g.First()).ToList();
        int Common(string a)
        {
            int best = 0;
            for (int i = 0; i < name.Length; i++)
                for (int len = Math.Min(name.Length - i, a.Length); len > best; len--)
                    if (a.Contains(name.Substring(i, len))) { best = len; break; }
            return best;
        }
        int need = name.Length >= 3 ? 2 : 1;
        return _allNames.Select(x => (x.Name, score: Common(x.Name))).Where(x => x.score >= need)
            .OrderByDescending(x => x.score).ThenBy(x => x.Name.Length).Take(max).Select(x => x.Name).ToList();
    }

    internal static string Unknown(string name)
    {
        var near = Similar(name);
        return $"游戏里没有叫「{name}」的物品。" + (near.Count > 0
            ? "名字相近的真实物品：" + string.Join("、", near) + "。要用就照抄其中一个名字，或者 ask 玩家要哪个。"
            : "换个准确的名字，或者 ask 玩家。");
    }

    // "工作台：木材 ×10；徒手。现在能做。" - every recipe for the item and whether the player can make it now.
    internal static string Describe(Player p, int type)
    {
        var recipes = RecipesFor(type).Take(3).ToList();
        if (recipes.Count == 0) return $"{Name(type)}没有配方（只能找到、买到或打怪掉落）。";
        p.AdjTiles();
        var lines = new List<string>();
        foreach (var r in recipes)
        {
            string stations = r.requiredTile.Count(t => t >= 0) == 0 ? "徒手就能做"
                : "在" + string.Join("和", r.requiredTile.Where(t => t >= 0).Select(Station)) + "旁边做";
            string conditions = r.Conditions.Count == 0 ? "" : "；条件：" + string.Join("、", r.Conditions.Select(c => c.Description.Value));
            var (lack, missingStations, unmet) = Check(p, r, 1);
            string now = lack.Count + missingStations.Count + unmet.Count == 0 ? "现在就能做"
                : "现在做不了：" + string.Join("；", new[] {
                    lack.Count > 0 ? "还差 " + string.Join("、", lack) : "",
                    missingStations.Count > 0 ? "要在" + string.Join("和", missingStations) + "旁边" : "",
                    unmet.Count > 0 ? string.Join("、", unmet) : "" }.Where(x => x.Length > 0));
            lines.Add($"{Name(type)} ×{r.createItem.stack}：{string.Join("、", r.requiredItem.Where(i => !i.IsAir).Select(i => $"{Name(i.type)} ×{i.stack}"))}；{stations}{conditions}。{now}。");
        }
        return "可用材料包含主背包与附近已关闭、未上锁的箱子。\n" + string.Join("\n", lines);
    }

    // ---- recipes -------------------------------------------------------------------------------
    // After gathering or placing a station: can `count` of `type` be made right now, and if not, what is missing?
    internal static string Progress(Player p, int type, int count)
    {
        var recipes = RecipesFor(type).ToList();
        if (recipes.Count == 0) return "";
        p.AdjTiles();   // a station placed this tick only counts after the stations are re-scanned
        var best = recipes.Select(r => Check(p, r, (count + r.createItem.stack - 1) / r.createItem.stack))
            .OrderBy(c => c.Lack.Count + c.Stations.Count + c.Conditions.Count).First();
        if (best.Lack.Count + best.Stations.Count + best.Conditions.Count == 0)
            return $"现在能做{Name(type)}了，下一步 craft 它。";
        var parts = new List<string>();
        if (best.Lack.Count > 0) parts.Add("还差 " + string.Join("、", best.Lack));
        if (best.Stations.Count > 0) parts.Add("要在" + string.Join("和", best.Stations) + "旁边");
        if (best.Conditions.Count > 0) parts.Add(string.Join("、", best.Conditions));
        return $"做{Name(type)}：" + string.Join("；", parts) + "。";
    }

    internal static IEnumerable<Recipe> RecipesFor(int type)
    {
        for (int i = 0; i < Recipe.numRecipes; i++)
        {
            var r = Main.recipe[i];
            if (r?.createItem != null && r.createItem.type == type && !r.Disabled) yield return r;
        }
    }

    // The platform chain deliberately uses only ordinary Wood in the main bag, with a live recipe.
    // It never pulls storage, substitutes rare woods or drops crafted output into the world.
    internal static Recipe? PlatformRecipe() => RecipesFor(ItemID.WoodPlatform).FirstOrDefault(r =>
        r.Mod == null && r.requiredItem.Count(i => !i.IsAir) == 1 &&
        r.requiredItem.Single(i => !i.IsAir).type == ItemID.Wood &&
        r.requiredItem.Single(i => !i.IsAir).stack > 0 && r.createItem.stack > 0 &&
        r.requiredTile.All(t => t < 0) && r.Conditions.Count == 0);

    internal static bool MakePlatforms(Player p, Recipe confirmed, int batches, out string message)
    {
        message = "配方已变化或不再可用。";
        if (batches < 1 || PlatformRecipe() != confirmed || confirmed.Disabled) return false;
        return MakeExact(p, confirmed, batches, out message);
    }

    // Shared atomic main-bag transaction for deterministic workshop chains.
    internal static bool MakeExact(Player p, Recipe confirmed, int batches, out string message)
    {
        message = "配方已变化或不再可用。";
        if (batches < 1 || confirmed.Disabled || !RecipesFor(confirmed.createItem.type).Contains(confirmed)) return false;
        p.AdjTiles();
        if (confirmed.requiredTile.Any(t => t >= 0 && !p.adjTile[t]) || confirmed.Conditions.Any(c => !c.IsMet()))
        { message = "制作站或合成条件不满足，未扣材料。"; return false; }
        int output = checked(confirmed.createItem.stack * batches);
        int outputType = confirmed.createItem.type;
        var bag = p.inventory.Take(50).Select(i => i.Clone()).ToArray();
        var costs = confirmed.requiredItem.Where(i => !i.IsAir).GroupBy(i => i.type)
            .ToDictionary(g => g.Key, g => checked(g.Sum(i => i.stack) * batches));
        var result = WorkshopInventory.Craft(bag.Select(i => new WorkshopStack(i.IsAir ? 0 : i.type, i.IsAir ? 0 : i.stack)).ToArray(),
            costs, outputType, output, confirmed.createItem.maxStack, out string failure);
        if (result == null) { message = failure + "，未扣材料。"; return false; }
        for (int i = 0; i < bag.Length; i++)
        {
            if (result[i].Count == 0) bag[i].TurnToAir();
            else
            {
                if (bag[i].type != result[i].Type) bag[i].SetDefaults(result[i].Type);
                bag[i].stack = result[i].Count;
            }
        }
        for (int i = 0; i < bag.Length; i++) p.inventory[i] = bag[i];
        message = "合成消耗 " + string.Join("、", costs.Select(x => $"{Name(x.Key)} ×{x.Value}")) + $"，{Name(outputType)}产出 {output}。";
        return true;
    }

    private static bool Accepts(Recipe r, int required, int candidate) => candidate == required ||
        r.acceptedGroups.Any(g => RecipeGroup.recipeGroups.TryGetValue(g, out var group) &&
            group.ContainsItem(required) && group.ContainsItem(candidate));

    private static IEnumerable<Item> Bag(Player p) => p.inventory.Take(50).Where(i => !i.IsAir);

    private static int Have(Player p, Recipe r, int required) =>
        Bag(p).Where(i => Accepts(r, required, i.type)).Sum(i => i.stack);

    internal static string Station(int tile)
    {
        foreach (var (type, item) in ContentSamples.ItemsByType)
            if (item.createTile == tile) return Name(type);
        return "制作站";
    }

    // What stops the player crafting `times` of this recipe right now: missing items, stations, conditions.
    private static (List<string> Lack, List<string> Stations, List<string> Conditions) Check(Player p, Recipe r, int times, bool nearby = true)
    {
        var lack = new List<string>();
        var allocation = IngredientAllocation.Plan(NearbyChests.Stock(p, nearby), NearbyChests.Requirements(r, times));
        foreach (var (type, count) in allocation.Missing) lack.Add($"{Name(type)} ×{count}");
        var stations = r.requiredTile.Where(t => t >= 0 && !p.adjTile[t]).Select(Station).Distinct().ToList();
        var conditions = r.Conditions.Where(c => !c.IsMet()).Select(c => c.Description.Value).ToList();
        return (lack, stations, conditions);
    }

    // When the materials are all there and only stations are missing: which station tiles (for the closest recipe).
    internal static List<int> MissingStations(Player p, int type, int count)
    {
        p.AdjTiles();
        foreach (var r in RecipesFor(type))
        {
            var c = Check(p, r, (count + r.createItem.stack - 1) / r.createItem.stack);
            if (c.Lack.Count == 0 && c.Conditions.Count == 0 && c.Stations.Count > 0)
                return r.requiredTile.Where(t => t >= 0 && !p.adjTile[t]).Distinct().ToList();
        }
        return new List<int>();
    }

    // Items that place this station tile: the player's own first, then everything that does.
    internal static IEnumerable<int> StationItems(Player p, int tile)
    {
        var mine = p.inventory.Take(58).Where(i => !i.IsAir && i.createTile == tile).Select(i => i.type).Distinct().ToList();
        return mine.Concat(ContentSamples.ItemsByType.Values.Where(i => i.createTile == tile && !mine.Contains(i.type))
            .OrderBy(i => i.type).Select(i => i.type));
    }

    // Craft `count` of `type` from the player's inventory, or say exactly what is missing.
    internal static (bool Done, string Message) Make(Player p, int type, int count)
    {
        var recipes = RecipesFor(type).ToList();
        if (recipes.Count == 0) return (false, $"{Name(type)}没有配方，做不了。");
        p.AdjTiles();   // vanilla only refreshes nearby stations while the inventory is open
        string pulled = "";
        // Prefer the main bag plus nearby ordinary chests. Each attempt is atomic.
        // Magic Storage keeps its existing fallback when no local recipe has enough material.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            foreach (var r in recipes)
            {
                int times = (count + r.createItem.stack - 1) / r.createItem.stack;
                if (r.requiredTile.Any(t => t >= 0 && !p.adjTile[t]) || r.Conditions.Any(c => !c.IsMet())) continue;
                if (NearbyChests.TryCraft(p, r, times, out string message, out bool ready)) return (true, message + pulled);
                if (ready) return (false, message + pulled);
            }
            if (attempt == 0) { pulled = PullFromStorage(p, recipes, count); if (pulled.Length == 0) break; }
        }
        // Explain with the recipe that is closest to working.
        var best = recipes.Select(r => (r, c: Check(p, r, (count + r.createItem.stack - 1) / r.createItem.stack)))
            .OrderBy(x => x.c.Lack.Count + x.c.Stations.Count + x.c.Conditions.Count).First();
        var parts = new List<string>();
        if (best.c.Lack.Count > 0) parts.Add("还差 " + string.Join("、", best.c.Lack));
        if (best.c.Stations.Count > 0) parts.Add("要站在" + string.Join("和", best.c.Stations) + "旁边");
        if (best.c.Conditions.Count > 0) parts.Add(string.Join("、", best.c.Conditions));
        return (false, $"{Name(type)} ×{count} 现在做不了：{string.Join("；", parts)}。");
    }

    // Magic Storage: when a recipe could be made here except for materials the storage holds, take exactly
    // the shortfall out into the inventory first (the same thing the player would do by hand).
    private static string PullFromStorage(Player p, List<Recipe> recipes, int count)
    {
        if (!ModTools.Storage.Available) return "";
        foreach (var r in recipes)
        {
            int times = (count + r.createItem.stack - 1) / r.createItem.stack;
            var (lack, stations, conditions) = Check(p, r, times, nearby: false);
            if (lack.Count == 0) return "";
            if (stations.Count + conditions.Count > 0) continue;
            var shortfall = r.requiredItem.Where(i => !i.IsAir)
                .Select(req => (type: req.type, need: req.stack * times - Have(p, r, req.type)))
                .Where(x => x.need > 0).ToList();
            if (shortfall.Any(x => ModTools.Storage.Count(x.type) < x.need)) continue;
            var taken = new List<string>();
            foreach (var (t, need) in shortfall)
            {
                int got = ModTools.Storage.Take(p, t, need);
                if (got > 0) taken.Add($"{Name(t)} ×{got}");
            }
            return taken.Count > 0 ? "（从魔法存储取了 " + string.Join("、", taken) + "）" : "";
        }
        return "";
    }

    // ---- "盖 NPC 小屋要什么" ---------------------------------------------------------------------
    internal static readonly Regex HouseWords = new("房|屋|住处|npc|NPC|宿舍");
    private static readonly Regex AsksMaterials = new("材料|需要|还差|差啥|差什么|要啥|要什么|准备|够不够|缺");

    internal static bool IsHouseQuestion(string text) => HouseWords.IsMatch(text) && AsksMaterials.IsMatch(text);
    internal static bool IsMakeHouseParts(string text) =>
        Regex.IsMatch(text, "(做齐|做好|做完|备齐|把).{0,4}(小屋|房子|房|屋).{0,2}(材料|东西)|(小屋|房子|房|屋)的?材料.{0,3}(做|搓|合成)");

    private static Dictionary<int, int> HouseNeeds()
    {
        var entry = StructureLibrary.List().First(e => e.Id == "preset:npc1");
        return entry.Load().Materials();
    }

    // Program-written: what one NPC room takes, what the player lacks, and what that costs in raw materials.
    internal static string HouseAnswer(Player p)
    {
        var needs = HouseNeeds();
        int Has(int t) => p.CountItem(t, 9999);
        var lines = new List<string> {
            "盖一间能住 NPC 的小屋（10×6，木头）要：" + string.Join("、", needs.Select(n => $"{Name(n.Key)} ×{n.Value}")) + "。" };
        var missing = needs.Where(n => Has(n.Key) < n.Value).ToDictionary(n => n.Key, n => n.Value - Has(n.Key));
        if (missing.Count == 0)
        {
            lines.Add("这些你背包里都有了。说「盖一间NPC房」，我照着蓝图盖。");
            return string.Join("\n", lines);
        }
        lines.Add("你还差：" + string.Join("、", missing.Select(m => $"{Name(m.Key)} ×{m.Value}")) + "。");
        // One level of recipes: how much raw material the missing parts take, against what is in the bag.
        var raw = new Dictionary<int, int>();
        var stations = new HashSet<string>();
        var uncraftable = new List<string>();
        foreach (var (type, amount) in missing)
        {
            var r = RecipesFor(type).FirstOrDefault();
            if (r == null) { uncraftable.Add(Name(type)); continue; }
            int times = (amount + r.createItem.stack - 1) / r.createItem.stack;
            foreach (var req in r.requiredItem.Where(i => !i.IsAir))
                raw[req.type] = raw.GetValueOrDefault(req.type) + req.stack * times;
            foreach (int t in r.requiredTile.Where(t => t >= 0)) stations.Add(Station(t));
        }
        if (raw.Count > 0)
        {
            int Needed(int t) => raw[t] + (needs.TryGetValue(t, out int direct) ? direct : 0);
            var shortRaw = raw.Keys.Where(t => Has(t) < Needed(t)).Select(t => $"{Name(t)} ×{Needed(t) - Has(t)}").ToList();
            lines.Add("这些用 " + string.Join("、", raw.Select(x => $"{Name(x.Key)} ×{x.Value}")) +
                (stations.Count > 0 ? "在" + string.Join("、", stations) + "做" : "做") + "，" +
                (shortRaw.Count == 0 ? "你背包里的材料够了。" : "还要再弄 " + string.Join("、", shortRaw) + "。"));
        }
        if (uncraftable.Count > 0) lines.Add(string.Join("、", uncraftable) + " 没法合成，得另外找。");
        p.AdjTiles();
        bool needsBench = stations.Contains(Station(TileID.WorkBenches)) && !p.adjTile[TileID.WorkBenches];
        lines.Add(needsBench
            ? "先说「做一个工作台」，放在身边，再说「做齐小屋材料」我帮你做；或者直接说「盖一间NPC房」。"
            : "说「做齐小屋材料」我帮你做；或者直接说「盖一间NPC房」，我照着蓝图盖。");
        return string.Join("\n", lines);
    }

    // Craft every missing part of the NPC room that can be made right here.
    internal static string MakeHouseParts(Player p)
    {
        var needs = HouseNeeds();
        var done = new List<string>();
        var failed = new List<string>();
        foreach (var (type, amount) in needs)
        {
            int lack = amount - p.CountItem(type, 9999);
            if (lack <= 0) continue;
            var (ok, message) = Make(p, type, lack);
            (ok ? done : failed).Add(ok ? $"{Name(type)} ×{lack}" : message);
        }
        if (done.Count == 0 && failed.Count == 0) return "小屋的材料你都有了，说「盖一间NPC房」就能开工。";
        string text = done.Count > 0 ? "做好了：" + string.Join("、", done) + "。" : "";
        if (failed.Count > 0) text += (text.Length > 0 ? "\n" : "") + string.Join("\n", failed);
        if (failed.Count == 0) text += "材料齐了，说「盖一间NPC房」就能开工。";
        return text;
    }
}
