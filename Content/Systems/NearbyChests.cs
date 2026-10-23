using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Xna.Framework;
using TerrarAI.Content.Companion;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace TerrarAI.Content.Systems;

internal static class NearbyChests
{
    internal const int Radius = 18;
    internal sealed record Box(int Id, Chest Chest);
    private static bool Usable(Player p, int id, Chest chest)
    {
        if (Main.netMode != NetmodeID.SinglePlayer || !p.active || p.dead || Main.gameMenu) return false;
        return Intact(id,chest)&&Vector2.DistanceSquared(p.Center,new Vector2((chest.x+1)*16,(chest.y+1)*16))<=Radius*Radius*256&&
            Chest.UsingChest(id)<0&&!Chest.IsLocked(chest.x,chest.y);
    }
    internal static bool Intact(int id,Chest chest)
    {
        if (id < 0 || id >= Main.chest.Length || !ReferenceEquals(Main.chest[id], chest) || chest.item == null || chest.item.Length != Chest.maxItems) return false;
        if (!WorldGen.InWorld(chest.x, chest.y, 3)) return false;
        var tile = Framing.GetTileSafely(chest.x, chest.y);
        if (!tile.HasTile || !TileID.Sets.BasicChest[tile.TileType] || Chest.FindChest(chest.x, chest.y) != id) return false;
        for (int x = chest.x; x < chest.x + 2; x++) for (int y = chest.y; y < chest.y + 2; y++)
            { var t = Framing.GetTileSafely(x, y); if (!t.HasTile || t.TileType != tile.TileType || t.IsActuated) return false; }
        return true;
    }
    // Position-only lookup shares entity/footprint checks with transfers, but open
    // and locked chests are still legitimate map locations. No contents are read.
    internal static IEnumerable<Box> Locations(double x,double y,int radius)
    {
        for(int id=0;id<Main.chest.Length;id++)
        {
            if(Main.chest[id] is not Chest chest||Math.Pow(chest.x+1-x,2)+Math.Pow(chest.y+1-y,2)>radius*radius)continue;
            bool valid;try{valid=Intact(id,chest);}catch(Exception ex)when(ex is not OutOfMemoryException){valid=false;}
            if(valid)yield return new(id,chest);
        }
    }
    private static List<Box> Find(Player p,IReadOnlySet<int>? allowed=null)
    {
        var boxes = new List<Box>();
        IEnumerable<int> candidates=allowed??(IEnumerable<int>)Enumerable.Range(0,Main.chest.Length);
        foreach(int i in candidates) if (i>=0&&i<Main.chest.Length&&Main.chest[i] is Chest c)
            { try { if (Usable(p, i, c)) boxes.Add(new(i, c)); } catch (Exception ex) when (ex is not OutOfMemoryException) {/* An unsupported chest is not accessible. */} }
        return boxes.OrderBy(b => Vector2.DistanceSquared(p.Center, new Vector2((b.Chest.x + 1) * 16, (b.Chest.y + 1) * 16))).ThenBy(b => b.Id).ToList();
    }
    internal static int Count(Player p) => Find(p).Count;
    private static string Label(Box b) => string.IsNullOrWhiteSpace(b.Chest.name) ? $"箱子 tile({b.Chest.x},{b.Chest.y})" : $"「{b.Chest.name}」 tile({b.Chest.x},{b.Chest.y})";
    internal static string Describe(Player p, string filter = "")
    {
        var boxes = Find(p);
        if (boxes.Count == 0) return $"{Radius}格内没有可用箱子（请先关闭箱子；上锁或失效的箱子不会读取）。";
        var lines = new List<string> { $"附近可用箱子 {boxes.Count} 个 · 半径{Radius}格" };
        foreach (var box in boxes)
        {
            var contents = box.Chest.item.Where(i => i != null && !i.IsAir && (filter.Length == 0 || Crafting.Name(i.type).Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .GroupBy(i => i.type).Select(g => $"{Crafting.Name(g.Key)}×{g.Sum(i => i.stack)}").ToList();
            if (filter.Length > 0 && contents.Count == 0) continue;
            lines.Add(Label(box) + "：" + (contents.Count == 0 ? "空" : string.Join("、", contents.Take(8)) + (contents.Count > 8 ? $" 等{contents.Count}种" : "")));
            if (lines.Count == 13) { lines.Add("其余箱子省略；可指定物品查询。"); break; }
        }
        if (lines.Count == 1) lines.Add($"没有找到「{filter}」。");
        return string.Join("\n", lines);
    }
    internal static IReadOnlySet<int> Ids(Player p)=>Find(p).Select(b=>b.Id).ToHashSet();
    internal static string Snapshot(Player p)
    {
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
        writer.Write(p.Bottom.ToTileCoordinates().X);writer.Write(p.Bottom.ToTileCoordinates().Y);
        foreach(var item in p.inventory.Take(50)){var bytes=Session.State(item);writer.Write(bytes.Length);writer.Write(bytes);}
        foreach(var box in Find(p))
        {
            writer.Write(box.Id);writer.Write(box.Chest.x);writer.Write(box.Chest.y);
            foreach(var item in box.Chest.item){var bytes=Session.State(item);writer.Write(bytes.Length);writer.Write(bytes);}
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    internal static Dictionary<int, int> Stock(Player p, bool includeChests = true,IReadOnlySet<int>? allowed=null)
    {
        var items = p.inventory.Take(50).Where(i => !i.IsAir);
        if (includeChests) items = items.Concat(Find(p,allowed).SelectMany(b => b.Chest.item).Where(i => i != null && !i.IsAir));
        return items.GroupBy(i => i.type).ToDictionary(g => g.Key, g => g.Sum(i => i.stack));
    }
    internal static IReadOnlyList<IngredientRequirement> Requirements(Recipe recipe, int batches) => recipe.requiredItem.Where(i => !i.IsAir)
        .GroupBy(i => i.type).Select(required => new IngredientRequirement(required.Key, checked(required.Sum(i => i.stack) * batches),
            recipe.acceptedGroups.Where(RecipeGroup.recipeGroups.ContainsKey).Select(gid => RecipeGroup.recipeGroups[gid])
                .Where(group => group.ContainsItem(required.Key)).SelectMany(group => group.ValidItems).Append(required.Key).Distinct().ToArray())).ToArray();

    internal static string Transfer(Player p, ChestRequest request,IReadOnlySet<int>? allowed=null)
    {
        if (request.Operation == ChestOperation.Query) return Describe(p, request.Items.FirstOrDefault()?.Name ?? "");
        if (Main.netMode != NetmodeID.SinglePlayer || !p.active || p.dead || CompanionMemorySystem.Session == Guid.Empty) return "存取箱子需要单人世界和存活角色。";
        var orders = new List<ContainerOrder>();
        foreach (var entry in request.Items)
        {
            int type = Crafting.ResolveExact(entry.Name);
            if (type == 0) return $"物品「{entry.Name}」不明确，请使用游戏中的物品全名。";
            if (orders.Any(o => o.Type == type)) return "同一物品请只写一次。";
            orders.Add(new(type, entry.Count));
        }
        try
        {
            var session = new Session(p,allowed);
            if (session.Boxes.Count == 0) return $"{Radius}格内没有未上锁且已关闭的可用箱子。";
            bool deposit = request.Operation == ChestOperation.Deposit;
            if (!session.Inventory.Transfer(deposit, orders, out string error)) return error;
            if (!session.Commit(out error)) return error;
            var receipts = session.Inventory.Receipts.GroupBy(r => (r.Box, r.Type)).Select(g => new { box = session.Boxes[g.Key.Box], type = g.Key.Type, count = g.Sum(r => r.Count) }).ToArray();
            Record("chest_transfer", new
            {
                direction = deposit ? "deposit" : "withdraw",
                entries = receipts.Select(r => new { r.box.Id, r.box.Chest.x, r.box.Chest.y, r.type, r.count }).ToArray()
            });
            return (deposit ? "存好了：" : "取好了：") + string.Join("；", receipts.Select(r => $"{Crafting.Name(r.type)}×{r.count} {(deposit ? "→" : "←")} {Label(r.box)}"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return "箱子存取未提交：" + ex.Message; }
    }

    // A normal craft is one bag+nearby-box transaction: no transient withdrawal,
    // no dropped output, no recipe-group ingredient can be spent twice.
    internal static bool TryCraft(Player p, Recipe recipe, int batches, out string message, out bool materialsReady,
        IReadOnlyDictionary<int,int>? exact=null,IReadOnlySet<int>? allowed=null)
    {
        message = ""; materialsReady = false;
        try
        {
            var session = new Session(p,allowed);
            var output = recipe.createItem.Clone(); output.stack = checked(output.stack * batches); int made = output.stack;
            var requirements=Requirements(recipe,batches);
            if(exact!=null)
            {
                var checkedCost=IngredientAllocation.Plan(exact,requirements);
                if(checkedCost.Missing.Count>0||checkedCost.Used.Values.Sum()!=exact.Values.Sum())
                {message="确认的具体材料已不符合当前配方。";materialsReady=true;return false;}
                requirements=exact.Select(x=>new IngredientRequirement(x.Key,x.Value,new[]{x.Key})).ToArray();
            }
            if (!session.Inventory.Craft(requirements, output, out message, out materialsReady)) return false;
            bool RecipeValid()
            {
                p.AdjTiles(); return !recipe.Disabled && Crafting.RecipesFor(recipe.createItem.type).Contains(recipe) &&
                recipe.requiredTile.All(t => t < 0 || p.adjTile[t]) && recipe.Conditions.All(c => c.IsMet());
            }
            if (!RecipeValid()) { message = "制作站或合成条件已变化，未扣材料。"; return false; }
            if (!session.Commit(out message, RecipeValid)) return false;
            var receipts = session.Inventory.Receipts.GroupBy(r => (r.Box, r.Type)).Select(g => new { box = session.Boxes[g.Key.Box], type = g.Key.Type, count = g.Sum(r => r.Count) }).ToArray();
            Record("chest_craft", new
            {
                item = recipe.createItem.type,
                made,
                recipe = recipe.RecipeIndex,
                entries = receipts.Select(r => new { r.box.Id, r.box.Chest.x, r.box.Chest.y, r.type, r.count }).ToArray()
            });
            message = $"做好了：{Crafting.Name(recipe.createItem.type)} ×{made}，放进你背包了。" +
                (receipts.Length == 0 ? "" : "（附近箱子补料：" + string.Join("；", receipts.Select(r => $"{Crafting.Name(r.type)}×{r.count}，{Label(r.box)}")) + "）");
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { materialsReady = true; message = "合成未提交：" + ex.Message; return false; }
    }

    private static void Record(string kind, object data)
    {
        try { CompanionMemorySystem.Record(kind, null, null, data); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ModContent.GetInstance<TerrarAI>().Logger.Warn("Chest receipt log failed after commit: " + ex.Message); }
    }

    private sealed class Items : IContainerItems<Item>
    {
        public Item Clone(Item i) => i.Clone();
        public int Type(Item i) => i.IsAir ? 0 : i.type;
        public int Count(Item i) => i.IsAir ? 0 : i.stack;
        public int Limit(Item i) => i.maxStack;
        public bool Favorite(Item i) => i.favorited;
        public bool CanStack(Item d, Item s) => d.type == s.type && ItemLoader.CanStack(d, s);
        public Item Split(Item s, int n) => ItemLoader.TransferWithLimit(s, n);
        public int Stack(Item d, Item s, int n) { ItemLoader.StackItems(d, s, out int moved, false, n); return moved; }
        public void Consume(Item i, int n) { i.stack -= n; if (i.stack == 0) i.TurnToAir(); }
    }
    private sealed class Session
    {
        private readonly Player _player;
        private readonly ControlStamp _stamp = CompanionCommandService.Capture();
        private readonly Item[] _bag;
        private readonly Item[][] _chests;
        private readonly byte[][] _bagState;
        private readonly byte[][][] _chestState;
        internal List<Box> Boxes { get; }
        internal ContainerInventory<Item> Inventory { get; }
        internal Session(Player p,IReadOnlySet<int>? allowed=null)
        {
            _player = p; Boxes = Find(p,allowed); _bag = p.inventory.Take(50).ToArray(); _chests = Boxes.Select(b => b.Chest.item.ToArray()).ToArray();
            _bagState = _bag.Select(State).ToArray(); _chestState = _chests.Select(b => b.Select(State).ToArray()).ToArray();
            Inventory = new(_bag, _chests, new Items());
        }
        internal static byte[] State(Item item)
        {
            using var stream = new MemoryStream();
            TagIO.ToStream(ItemIO.Save(item), stream, false);
            using var writer = new BinaryWriter(stream);
            writer.Write(item.type); writer.Write(item.stack); writer.Write(item.prefix);
            writer.Write(item.maxStack); writer.Write(item.favorited);
            return stream.ToArray();
        }
        internal bool Commit(out string error, Func<bool>? guard = null)
        {
            error = "世界、背包、箱子或合成条件已变化，未提交，请重试。";
            // Finish serialization and mod SaveData calls on all proposed slots before
            // validating live state. No fallible serialization occurs after validation.
            var bagChanges = Enumerable.Range(0, _bag.Length).Where(i => !State(Inventory.Bag[i]).SequenceEqual(_bagState[i])).ToArray();
            var chestChanges = Enumerable.Range(0, Boxes.Count).SelectMany(b => Enumerable.Range(0, _chests[b].Length)
                .Where(i => !State(Inventory.Boxes[b][i]).SequenceEqual(_chestState[b][i])).Select(i => (b, i))).ToArray();
            if (!Inventory.ReadyToCommit || !_stamp.Matches(CompanionMemorySystem.Session, CompanionCommandService.Revision) || CompanionMemorySystem.Session == Guid.Empty ||
                Main.netMode != NetmodeID.SinglePlayer || !_player.active || _player.dead || Main.gameMenu ||
                !ReferenceEquals(Main.player[_player.whoAmI], _player) || guard?.Invoke() == false) return false;
            for (int i = 0; i < _bag.Length; i++) if (!ReferenceEquals(_player.inventory[i], _bag[i]) || !State(_player.inventory[i]).SequenceEqual(_bagState[i])) return false;
            for (int b = 0; b < Boxes.Count; b++)
            {
                var box = Boxes[b]; if (!Usable(_player, box.Id, box.Chest)) return false;
                for (int i = 0; i < _chests[b].Length; i++) if (!ReferenceEquals(box.Chest.item[i], _chests[b][i]) || !State(box.Chest.item[i]).SequenceEqual(_chestState[b][i])) return false;
            }
            if (!_stamp.Matches(CompanionMemorySystem.Session, CompanionCommandService.Revision) || guard?.Invoke() == false) return false;
            foreach (int i in bagChanges) _player.inventory[i] = Inventory.Bag[i];
            foreach (var (b, i) in chestChanges) Boxes[b].Chest.item[i] = Inventory.Boxes[b][i];
            try { Recipe.FindRecipes(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { ModContent.GetInstance<TerrarAI>().Logger.Warn("Recipe refresh failed after chest commit: " + ex.Message); }
            error = ""; return true;
        }
    }
}
