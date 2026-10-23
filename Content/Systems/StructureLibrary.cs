using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TerrarAI.Content.Companion;
using TerrarAI.Content.Systems.Vendor;
using Terraria;
using Terraria.Enums;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using Terraria.ObjectData;

namespace TerrarAI.Content.Systems;

// Where blueprints come from: built-in presets, and structures saved with Quality of Terraria's
// (更好的体验) Construct Wand, read straight from its .qotstruct files (tModLoader tag format).
internal static class StructureLibrary
{
    internal sealed record Entry(string Id, string Name, string Source, string Size, Func<StructurePlan> Load);

    internal static string QotFolder => Path.Combine(ModLoader.ModPath, "ImproveGame");

    internal static List<Entry> List()
    {
        var list = new List<Entry>();
        foreach (int rooms in new[] { 1, 2, 3 })
        {
            int width = 1 + rooms * 9;
            list.Add(new($"preset:npc{rooms}", rooms == 1 ? "NPC 小屋（木）" : $"NPC 联排 ×{rooms}（木）", "预设",
                $"{width}×6", () => StructurePresets.NpcRooms(rooms, WoodPalette)));
        }
        try
        {
            if (Directory.Exists(QotFolder))
                foreach (var file in new DirectoryInfo(QotFolder).GetFiles("*.qotstruct")
                    .OrderByDescending(f => f.LastWriteTimeUtc).Take(30))
                {
                    string path = file.FullName;
                    list.Add(new("qot:" + path, Path.GetFileNameWithoutExtension(path), "更好的体验", "",
                        () => LoadQot(path)));
                }
        }
        catch (Exception ex) { ModContent.GetInstance<TerrarAI>().Logger.Warn("Blueprint folder unreadable: " + ex.Message); }
        return list;
    }

    internal static readonly StructurePresets.Palette WoodPalette = new(
        TileID.WoodBlock, ItemID.Wood, WallID.Wood, ItemID.WoodWall,
        TileID.ClosedDoor, ItemID.WoodenDoor, TileID.Tables, ItemID.WoodenTable,
        TileID.Chairs, ItemID.WoodenChair, TileID.Torches, ItemID.Torch);

    // ---- item lookup: which item places this tile / wall --------------------------------------
    private static Dictionary<(int tile, int style), int>? _tileItems;
    private static Dictionary<int, int>? _tileAnyItem, _wallItems;

    private static void BuildLookups()
    {
        if (_tileItems != null) return;
        _tileItems = new(); _tileAnyItem = new(); _wallItems = new();
        foreach (var (type, item) in ContentSamples.ItemsByType)
        {
            if (item.createTile >= 0)
            {
                _tileItems.TryAdd((item.createTile, item.placeStyle), type);
                _tileAnyItem.TryAdd(item.createTile, type);
            }
            if (item.createWall > 0) _wallItems.TryAdd(item.createWall, type);
        }
    }

    internal static int ItemForTile(int tile, int style)
    {
        BuildLookups();
        if (tile == TileID.OpenDoor) tile = TileID.ClosedDoor;
        if (TileID.Sets.Grass[tile]) tile = TileID.Dirt;
        if (_tileItems!.TryGetValue((tile, style), out int item)) return item;
        return tile >= TileID.Count && _tileAnyItem!.TryGetValue(tile, out item) ? item : 0;
    }

    internal static int ItemForWall(int wall)
    {
        BuildLookups();
        return _wallItems!.TryGetValue(wall, out int item) ? item : 0;
    }

    internal static void Unload() { _tileItems = null; _tileAnyItem = null; _wallItems = null; }

    // ---- frame -> place style ---------------------------------------------------------------
    internal static int StyleOf(int type, int frameX, int frameY)
    {
        if (TileID.Sets.Platforms[type]) return frameY / 18;
        if (type < TileID.Count && QotPlaceStyle.Special(type, frameX, frameY) is int special) return special;
        var data = TileObjectData.GetTileData(type, 0);
        if (data == null) return 0;
        int unitX = frameX / Math.Max(1, data.CoordinateFullWidth), unitY = frameY / Math.Max(1, data.CoordinateFullHeight);
        int wrap = data.StyleWrapLimit == 0 ? 1 : data.StyleWrapLimit;
        int unit = data.StyleHorizontal ? unitY * wrap + unitX : unitX * wrap + unitY;
        int style = unit / Math.Max(1, data.StyleMultiplier);
        if (data.StyleLineSkip > 1)
            style = data.StyleHorizontal ? unitY / data.StyleLineSkip * wrap + unitX : unitX / data.StyleLineSkip * wrap + unitY;
        return style;
    }

    // ---- .qotstruct reader ------------------------------------------------------------------
    // Layout (Quality of Terraria FileOperator/QoLStructure): Width/Height are inclusive spans, cells are
    // stored column by column (index = y + x * (Height + 1)); modded tiles/walls are indexed through
    // EntriesName ("Mod/Name" + 't' or 'w') / EntriesType.
    internal static StructurePlan LoadQot(string path)
    {
        var tag = TagIO.FromFile(path);
        int w = tag.GetShort("Width") + 1, h = tag.GetShort("Height") + 1;
        var names = tag.GetList<string>("EntriesName");
        var ids = tag.GetList<ushort>("EntriesType");
        var modTiles = new Dictionary<int, int>();
        var modWalls = new Dictionary<int, int>();
        var missingMods = new HashSet<string>();
        for (int i = 0; i < names.Count && i < ids.Count; i++)
        {
            string name = names[i];
            if (name.EndsWith('t') && ModContent.TryFind(name[..^1], out ModTile tile)) modTiles[ids[i]] = tile.Type;
            else if (name.EndsWith('w') && ModContent.TryFind(name[..^1], out ModWall wall)) modWalls[ids[i]] = wall.Type;
            else missingMods.Add(name.Split('/')[0]);
        }
        var cells = tag.GetList<TagCompound>("StructureData");
        if (cells.Count < w * h) throw new InvalidDataException("蓝图文件不完整。");

        var steps = new List<StructureStep>();
        var skipped = new Dictionary<string, int>();
        void Skip(string why) => skipped[why] = skipped.GetValueOrDefault(why) + 1;
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                var c = cells[y + x * h];
                byte extra = c.ContainsKey("ExtraDatas") ? c.GetByte("ExtraDatas") : (byte)0;
                byte extra2 = c.ContainsKey("ExtraDatas2") ? c.GetByte("ExtraDatas2") : (byte)0;
                if ((extra2 & 0xF8) != 0) Skip("电线／促动器（下个版本支持）");
                if (c.TryGet("WallIndex", out short wallIndex) && wallIndex >= 0)
                {
                    bool vanilla = (extra & 2) == 0;
                    int wall = vanilla ? wallIndex : modWalls.GetValueOrDefault(wallIndex, -1);
                    int item = wall > 0 ? ItemForWall(wall) : 0;
                    if (item > 0) steps.Add(new(x, y, StepKind.Wall, wall, 0, item, Paint: c.ContainsKey("WallColor") ? c.GetByte("WallColor") : (byte)0));
                    else Skip(wall < 0 ? "缺少模组的墙" : "没有对应物品的墙（如天然土墙）");
                }
                if (!c.TryGet("TileIndex", out short tileIndex) || tileIndex < 0) continue;
                bool vanillaTile = (extra & 1) == 0;
                int type = vanillaTile ? tileIndex : modTiles.GetValueOrDefault(tileIndex, -1);
                if (type < 0) { Skip("缺少模组的方块"); continue; }
                int fx = c.GetShort("TileFrameX"), fy = c.GetShort("TileFrameY");
                byte paint = c.ContainsKey("TileColor") ? c.GetByte("TileColor") : (byte)0;
                if (TileID.Sets.BasicChest[type] || TileID.Sets.BasicDresser[type]) { Skip("箱子／梳妆台（下个版本支持）"); continue; }
                int style = StyleOf(type, fx, fy);
                var data = Main.tileFrameImportant[type] && !TileID.Sets.Platforms[type] ? TileObjectData.GetTileData(type, style) : null;
                if (data != null && (data.Width > 1 || data.Height > 1))
                {
                    // Only the origin cell of a multi-tile object becomes a step.
                    int subX = fx % Math.Max(1, data.CoordinateFullWidth) / 18, subY = fy % Math.Max(1, data.CoordinateFullHeight) / 18;
                    if (subX != data.Origin.X || subY != data.Origin.Y) continue;
                    int item = ItemForTile(type, style);
                    if (item <= 0) { Skip("没有对应物品的家具"); continue; }
                    int dir = data.Direction == TileObjectDirection.None ? 0
                        : fx / Math.Max(1, data.CoordinateFullWidth) % 2 == 1 ? 1 : -1;
                    steps.Add(new(x, y, StepKind.Furniture, type, style, item, Paint: paint, Width: data.Width, Height: data.Height,
                        Direction: dir, OriginX: data.Origin.X, OriginY: data.Origin.Y));
                    continue;
                }
                int blockItem = ItemForTile(type, style);
                if (blockItem <= 0) { Skip("没有对应物品的方块（如天然矿物、树）"); continue; }
                var kind = Main.tileFrameImportant[type] && !TileID.Sets.Platforms[type] ? StepKind.Furniture : StepKind.Block;
                byte shape = (extra & 0x10) != 0 ? (byte)0 : (extra & 0x20) != 0 ? (byte)1
                    : (byte)((extra & 0x80) != 0 ? ((extra & 0x40) != 0 ? 4 : 2) : ((extra & 0x40) != 0 ? 5 : 3));
                if (kind == StepKind.Furniture) shape = 0;
                steps.Add(new(x, y, kind, type, style, blockItem, Shape: shape, Paint: paint));
            }
        var notes = skipped.Select(p => $"{p.Key} ×{p.Value}").ToList();
        if (missingMods.Count > 0) notes.Add("未加载的模组：" + string.Join("、", missingMods));
        return new StructurePlan(Path.GetFileNameWithoutExtension(path), "更好的体验", w, h, steps, notes);
    }
}
