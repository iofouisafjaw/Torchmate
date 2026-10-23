using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace TerrarAI.Content.Systems;

// Other mods' knowledge, by soft reference: nothing here is required, and nothing breaks when a mod
// is missing. Also exports the game's own recipe table, which already contains every loaded mod.
public sealed class ModIntegration : ModSystem
{
    private static Dictionary<string, Dictionary<string, object>>? _checklist;
    private static Dictionary<string, Func<bool>> _available = new();
    private static bool _exported;
    internal static string GameDataPath => Path.Combine(Main.SavePath, "ModData", "TerrarAI", "knowledge", "gamedata.json");

    public override void PostAddRecipes()
    {
        _checklist = null;
        _available = new();
        try
        {
            // Boss Checklist's documented cross-mod API (see its BossChecklistIntegrationExample.cs).
            if (!ModLoader.TryGetMod("BossChecklist", out Mod checklist) || checklist.Version < new Version(1, 6)) return;
            if (checklist.Call("GetBossInfoDictionary", Mod, "1.6") is Dictionary<string, Dictionary<string, object>> entries)
                _checklist = entries;
            _available = ReadAvailability(checklist);
            Mod.Logger.Info($"Boss Checklist integration: {_checklist?.Count ?? 0} entries.");
        }
        catch (Exception ex)
        {
            _checklist = null;
            Mod.Logger.Warn("Boss Checklist integration failed: " + ex.Message);
        }
    }

    public override void OnWorldLoad()
    {
        if (_exported) return;
        _exported = true;
        try { ExportGameData(); }
        catch (Exception ex) { Mod.Logger.Warn("Game data export failed: " + ex.Message); }
    }

    public override void Unload()
    {
        _checklist = null;
        _available = new();
        _exported = false;
    }

    // The API omits per-world availability (e.g. Eater of Worlds vs Brain of Cthulhu); read it if
    // the internal field is still where it was, otherwise treat every entry as available.
    private static Dictionary<string, Func<bool>> ReadAvailability(Mod checklist)
    {
        var result = new Dictionary<string, Func<bool>>();
        try
        {
            const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var tracker = checklist.GetType().GetField("bossTracker", Any)?.GetValue(null);
            if (tracker?.GetType().GetField("SortedEntries", Any)?.GetValue(tracker) is IEnumerable list)
                foreach (var entry in list)
                {
                    var type = entry.GetType();
                    if (type.GetProperty("Key", Any)?.GetValue(entry) is string key &&
                        type.GetField("available", Any)?.GetValue(entry) is Func<bool> available)
                        result[key] = available;
                }
        }
        catch { }
        return result;
    }

    private static bool Safe(Func<bool>? f) { try { return f?.Invoke() ?? false; } catch { return false; } }
    private static bool Flag(Dictionary<string, object> e, string key) => e.TryGetValue(key, out var v) && v is bool b && b;

    private static string[] ItemNames(Dictionary<string, object> e, string key, int max) =>
        e.TryGetValue(key, out var v) && v is List<int> ids
            ? ids.Where(id => id > 0).Select(id => Lang.GetItemNameValue(id)).Where(n => !string.IsNullOrEmpty(n)).Distinct().Take(max).ToArray()
            : Array.Empty<string>();

    // "[i:29]" -> item name, "[c/FF0000:text]" -> text: chat tags mean nothing to the model.
    private static readonly Regex ItemTag = new(@"\[i(?:/[^:\]]*)?:([^\]]+)\]");
    private static readonly Regex OtherTag = new(@"\[[a-zA-Z]+(?:/[^:\]]*)?:([^\]]*)\]");
    private static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = ItemTag.Replace(text, m => int.TryParse(m.Groups[1].Value, out int id) ? Lang.GetItemNameValue(id)
            : ModContent.TryFind(m.Groups[1].Value, out ModItem item) ? item.DisplayName.Value : m.Groups[1].Value);
        return OtherTag.Replace(text, "$1").Trim();
    }

    // Boss Checklist entries for this world (game thread only: "downed" reads world state).
    internal static object[]? ChecklistForBrain()
    {
        if (_checklist == null) return null;
        var list = new List<object>();
        foreach (var (key, e) in _checklist)
        {
            if (_available.TryGetValue(key, out var available) && !Safe(available)) continue;
            string spawnInfo = "";
            try { spawnInfo = Clean((e.GetValueOrDefault("spawnInfo") as Func<LocalizedText>)?.Invoke()?.Value); } catch { }
            list.Add(new
            {
                key,
                mod = e.GetValueOrDefault("modSource") as string ?? "",
                name = (e.GetValueOrDefault("displayName") as LocalizedText)?.Value ?? key,
                progression = Convert.ToSingle(e.GetValueOrDefault("progression") ?? 0f),
                type = Flag(e, "isBoss") ? "boss" : Flag(e, "isMiniboss") ? "miniboss" : Flag(e, "isEvent") ? "event" : "other",
                downed = Safe(e.GetValueOrDefault("downed") as Func<bool>),
                spawnInfo,
                spawnItems = ItemNames(e, "spawnItems", 4),
                loot = ItemNames(e, "loot", 14)
            });
        }
        return list.ToArray();
    }

    // Every recipe in the game (vanilla + all loaded mods) in the same text format as the Wiki index,
    // plus names and tooltips of modded items, written once per session for the brain to read.
    private static void ExportGameData()
    {
        var stationNames = new Dictionary<int, string>();
        foreach (var item in ContentSamples.ItemsByType.Values)
            if (item.createTile >= 0 && !stationNames.ContainsKey(item.createTile) && !string.IsNullOrEmpty(item.Name))
                stationNames[item.createTile] = item.Name;
        string Station(int tile) => stationNames.TryGetValue(tile, out var n) ? n
            : tile < TileID.Count ? TileID.Search.GetName(tile) : TileLoader.GetTile(tile)?.Name ?? tile.ToString();

        var recipes = new List<string>();
        for (int r = 0; r < Recipe.numRecipes; r++)
        {
            var recipe = Main.recipe[r];
            if (recipe?.createItem == null || recipe.createItem.IsAir) continue;
            string Ingredient(Item i)
            {
                var group = recipe.acceptedGroups.Select(id => RecipeGroup.recipeGroups.TryGetValue(id, out var g) ? g : null)
                    .FirstOrDefault(g => g != null && g.ContainsItem(i.type));
                return (group != null ? group.GetText() : i.Name) + " ×" + i.stack;
            }
            string stations = recipe.requiredTile.Count == 0 ? "徒手" : string.Join(" 和 ", recipe.requiredTile.Select(Station));
            string conditions = recipe.Conditions.Count == 0 ? ""
                : "；条件：" + string.Join("、", recipe.Conditions.Select(c => c.Description.Value));
            recipes.Add($"配方：{recipe.createItem.Name} ×{recipe.createItem.stack}；材料：" +
                $"{string.Join("、", recipe.requiredItem.Where(i => !i.IsAir).Select(Ingredient))}；制作站：{stations}{conditions}。");
        }

        var modItems = new List<object>();
        foreach (var (type, item) in ContentSamples.ItemsByType)
        {
            if (type < ItemID.Count || item.IsAir || string.IsNullOrEmpty(item.Name)) continue;
            var tip = Lang.GetTooltip(type);
            string tooltip = string.Join(" ", Enumerable.Range(0, tip.Lines).Select(tip.GetLine));
            modItems.Add(new { name = item.Name, key = item.ModItem?.Name, mod = item.ModItem?.Mod.Name, tooltip = Clean(tooltip),
                damage = item.damage, defense = item.defense, pick = item.pick, rare = item.rare, accessory = item.accessory });
        }

        Directory.CreateDirectory(Path.GetDirectoryName(GameDataPath)!);
        File.WriteAllText(GameDataPath, JsonSerializer.Serialize(new
        {
            version = Main.versionNumber, language = Language.ActiveCulture.Name,
            mods = ModLoader.Mods.Select(m => m.Name).ToArray(), recipes, modItems
        }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) }));
    }
}
