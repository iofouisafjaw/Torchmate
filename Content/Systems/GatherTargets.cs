using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrarAI.Content.Systems;

// The program ranks exposed gathering candidates. Native movement remains the
// authority for reachability; selecting ore never authorizes digging a tunnel.
internal static class GatherTargets
{
    private const int Radius = 28;

    // Shared protection for quantitative material jobs. A tree is an entire object,
    // so check the trunk and branches rather than only the bottom tile.
    internal static bool SafeForestTree(Point p,Point center,Func<int,int,bool>? excluded=null)
    {
        if(!WorldGen.InWorld(p.X,p.Y,10)||Math.Abs(p.X-center.X)>Radius||Math.Abs(p.Y-center.Y)>Radius)return false;
        var ground=Framing.GetTileSafely(p.X,p.Y+1);
        if(!ground.HasTile||ground.TileType!=TileID.Grass)return false;
        for(int x=p.X-3;x<=p.X+3;x++)for(int y=p.Y;y>=Math.Max(10,p.Y-80);y--)
        {
            var t=Framing.GetTileSafely(x,y);
            if(t.HasTile&&t.TileType!=TileID.Trees&&Main.tileFrameImportant[t.TileType]&&!Main.tileCut[t.TileType])return false;
            if(t.HasTile&&t.TileType==TileID.Trees&&(excluded?.Invoke(x,y)==true||t.WallType!=WallID.None||HousingInspector.Hazard(t)))return false;
        }
        var tree=Framing.GetTileSafely(p.X,p.Y);return tree.HasTile&&tree.TileType==TileID.Trees;
    }

    private static readonly Dictionary<string, ushort[]> OreTiles = new()
    {
        ["铜"] = new[] { TileID.Copper }, ["锡"] = new[] { TileID.Tin }, ["铁"] = new[] { TileID.Iron },
        ["铅"] = new[] { TileID.Lead }, ["银"] = new[] { TileID.Silver }, ["钨"] = new[] { TileID.Tungsten },
        ["金"] = new[] { TileID.Gold }, ["铂金"] = new[] { TileID.Platinum }, ["魔矿"] = new[] { TileID.Demonite },
        ["猩红矿"] = new[] { TileID.Crimtane }, ["陨石"] = new[] { TileID.Meteorite }, ["狱石"] = new[] { TileID.Hellstone },
        ["黑曜石"] = new[] { TileID.Obsidian },
        ["钴"] = new[] { TileID.Cobalt }, ["钯金"] = new[] { TileID.Palladium },
        ["秘银"] = new[] { TileID.Mythril }, ["山铜"] = new[] { TileID.Orichalcum },
        ["精金"] = new[] { TileID.Adamantite }, ["钛金"] = new[] { TileID.Titanium },
        ["叶绿"] = new[] { TileID.Chlorophyte }, ["夜明"] = new[] { TileID.LunarOre },
    };

    private static readonly Dictionary<string, ushort[]> BlockTiles = new()
    {
        ["石块"] = new[] { TileID.Stone }, ["土块"] = new[] { TileID.Dirt }, ["沙块"] = new[] { TileID.Sand },
        ["泥块"] = new[] { TileID.Mud }, ["粘土块"] = new[] { TileID.ClayBlock }, ["雪块"] = new[] { TileID.SnowBlock },
        ["冰块"] = new[] { TileID.IceBlock },
    };

    // Share the executor's power rules, including modded tile requirements.
    private static int MinPick(ushort type) => ToolSelector.GetTileStrength(type);

    internal static bool KnownOre(string? word) => word != null && (OreTiles.ContainsKey(word) || BlockTiles.ContainsKey(word));
    private static string OreWord(string name)=>name.Trim().Replace("矿石","").TrimEnd('矿');
    internal static IReadOnlyList<ushort> LocateOreTypes(string name)=>OreTiles.FirstOrDefault(x=>OreWord(x.Key)==OreWord(name)).Value??Array.Empty<ushort>();
    internal static string? OreLabel(int type)=>OreTiles.FirstOrDefault(x=>x.Value.Contains((ushort)type)).Key is string word?
        word is "黑曜石" or "陨石"?word:word.EndsWith("矿")?word:word+"矿":null;

    // Where to look from: the companion by default, the player for "我旁边", one side for "左边/右边".
    internal static (Point center, int side) Origin(NPC agent, Player? player, string anchor)
    {
        var agentTile = agent.Bottom.ToTileCoordinates();
        if (player == null) return (agentTile, 0);
        var playerTile = player.Bottom.ToTileCoordinates();
        return anchor switch
        {
            "player" => (playerTile, 0),
            "left" => (playerTile, -1),
            "right" => (playerTile, 1),
            _ => (agentTile, 0)
        };
    }

    internal static Point? NextTree(NPC agent, Point center, int side, ICollection<Point> skip)
    {
        int groundY = agent.Bottom.ToTileCoordinates().Y;
        return TreeHelper.FindTreeBasesNear(center, Radius)
            .Where(t => !skip.Contains(t) && TreeHelper.DoesTreeExist(t) && !TaskTargetClaims.Occupied(t))
            .Where(t => side == 0 || Math.Sign(t.X - center.X) == side)
            .Select(t => (t, cost: Cost(center, groundY, t.X, t.Y + 1, exposed: true)))
            .Where(x => x.cost < int.MaxValue)
            .OrderBy(x => x.cost).Select(x => (Point?)x.t).FirstOrDefault();
    }

    internal static Point? NextOre(NPC agent, Point center, int side, ICollection<Point> skip, int pickPower, string? oreWord,Func<Point,bool>? accept=null,bool exposedOnly=true)
    {
        int groundY = agent.Bottom.ToTileCoordinates().Y;
        bool blocks = oreWord != null && BlockTiles.ContainsKey(oreWord);
        ushort[]? wanted = oreWord == null ? null : blocks ? BlockTiles[oreWord] : OreTiles.GetValueOrDefault(oreWord);
        Point? best = null;
        int bestCost = int.MaxValue;
        for (int y = center.Y - Radius; y <= center.Y + Radius; y++)
            for (int x = center.X - Radius; x <= center.X + Radius; x++)
            {
                if (x < 2 || y < 2 || x >= Main.maxTilesX - 2 || y >= Main.maxTilesY - 2) continue;
                if (side != 0 && Math.Sign(x - center.X) != side) continue;
                var tile = Framing.GetTileSafely(x, y);
                if (!tile.HasTile || tile.IsActuated || (!blocks && !TileID.Sets.Ore[tile.TileType])) continue;
                if (wanted != null && !wanted.Contains(tile.TileType) || MinPick(tile.TileType) > pickPower) continue;
                var p = new Point(x, y);
                if (skip.Contains(p)||accept?.Invoke(p)==false) continue;
                // A proximity penalty cannot make sealed ore reachable. Do not
                // spend task decisions on every tile of the same buried vein.
                if (exposedOnly && !Exposed(x, y)) continue;
                int cost = Cost(center, groundY, x, y, exposed: true);
                if (cost < bestCost) { bestCost = cost; best = p; }
            }
        return best;
    }

    internal static bool TooHardFor(string? oreWord, int pickPower) =>
        oreWord != null && OreTiles.TryGetValue(oreWord, out var types) && types.All(t => MinPick(t) > pickPower);

    // Walking distance proxy: sideways is cheap, climbing or digging is expensive, far above/below is out.
    private static int Cost(Point center, int groundY, int x, int y, bool exposed)
    {
        int dx = Math.Abs(x - center.X), dy = Math.Abs(y - groundY);
        if (dx > Radius || dy > 14) return int.MaxValue;
        return dx + 4 * Math.Max(0, dy - 2) + (exposed ? 0 : 12);
    }

    private static bool Exposed(int x, int y)
    {
        foreach (var (ox, oy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            var n = Framing.GetTileSafely(x + ox, y + oy);
            if (!n.HasTile || n.IsActuated || !Main.tileSolid[n.TileType] || Main.tileSolidTop[n.TileType]) return true;
        }
        return false;
    }
}
