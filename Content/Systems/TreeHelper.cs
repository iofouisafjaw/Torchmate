// TerrarAI replacement implementation, 2026-10-20. See docs/source-replacement.md.
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
namespace TerrarAI.Content.Systems;
public static class TreeHelper
{
    public static bool IsTreeTile(Point at)
    {
        if (!WorldGen.InWorld(at.X, at.Y, 1)) return false;
        var cell = Framing.GetTileSafely(at.X, at.Y);
        return cell.HasTile && cell.TileType < TileID.Sets.IsATreeTrunk.Length && TileID.Sets.IsATreeTrunk[cell.TileType];
    }
    public static Point? FindTreeBase(Point at)
    {
        if (!IsTreeTile(at)) return null;
        while (at.Y + 1 < Main.maxTilesY - 1 && IsTreeTile(new(at.X, at.Y + 1))) at.Y++;
        return at;
    }
    public static Point? FindTreeCenter(Point at)
    {
        if (FindTreeBase(at) is not Point ground) return null;
        int Height(int x)
        { int height = 0; while (height < 80 && IsTreeTile(new(x, ground.Y - height))) height++; return height; }
        return Enumerable.Range(ground.X - 2, 5).Where(x => IsTreeTile(new(x, ground.Y)))
            .OrderByDescending(Height).ThenBy(x => Math.Abs(x - at.X)).Select(x => (Point?)new Point(x, ground.Y)).FirstOrDefault();
    }
    public static bool DoesTreeExist(Point at) => Enumerable.Range(Math.Max(1, at.Y - 10),
        Math.Max(0, Math.Min(Main.maxTilesY - 2, at.Y + 2) - Math.Max(1, at.Y - 10) + 1)).Any(y => IsTreeTile(new(at.X, y)));
    public static List<Point> FindTreeBasesNear(Point centerTile, int radius)
    {
        var unique = new HashSet<Point>();
        int range = Math.Clamp(radius, 0, 100);
        for (int x = Math.Max(1, centerTile.X - range); x <= Math.Min(Main.maxTilesX - 2, centerTile.X + range); x++)
            for (int y = Math.Max(1, centerTile.Y - range); y <= Math.Min(Main.maxTilesY - 2, centerTile.Y + range); y++)
                if (IsTreeTile(new(x, y)) && FindTreeCenter(new(x, y)) is Point root) unique.Add(root);
        return unique.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
    }
}
