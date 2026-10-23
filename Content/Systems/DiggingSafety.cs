using System;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerrarAI.Content.Companion;
namespace TerrarAI.Content.Systems;

// Shared with the existing bounded shaft. All mutations still use MineAction.
internal static class DiggingSafety
{
    private static bool Trap(int type)=>type is TileID.Spikes or TileID.WoodenSpikes or TileID.Traps or TileID.Boulder or TileID.Explosives;
    private static bool Falling(int type)=>type is TileID.Sand or TileID.Silt or TileID.Slush or TileID.Ebonsand or TileID.Crimsand or TileID.Pearlsand;
    private static string? Hazard(Tile t)=>t.LiquidAmount>0?"液体":
        t.HasTile&&!t.IsActuated&&Trap(t.TileType)?"陷阱":t.RedWire||t.BlueWire||t.GreenWire||t.YellowWire?"电路":null;
    internal static DigCell ReadTunnel(Player player,int x,int y)
    {
        var cell=Read(player,x,y);
        if(!cell.Hazard&&!cell.Protected&&WorldGen.InWorld(x,y,12))
        {
            var tile=Framing.GetTileSafely(x,y);
            // Decorative cuttable plants are passable space, not permission to
            // chop a tree or mine a structural block. Leave them untouched.
            if(tile.HasTile&&Main.tileCut[tile.TileType]&&!Main.tileSolid[tile.TileType])return new(x,y);
        }
        return cell.Type is TileID.Dirt or TileID.Grass?cell with{Substrate=TileID.Dirt}:cell;
    }
    internal static DigCell Read(Player player,int x,int y)
    {
        if(!WorldGen.InWorld(x,y,12))return new(x,y,Protected:true,Hazard:true);
        var t=Framing.GetTileSafely(x,y);var pick=ToolSelector.FindInventoryTool(player,ToolType.Pickaxe);
        bool natural=t.TileType is TileID.Dirt or TileID.Grass or TileID.Stone or TileID.Mud or TileID.ClayBlock or TileID.SnowBlock or TileID.IceBlock||
            t.TileType<TileID.Sets.Ore.Length&&TileID.Sets.Ore[t.TileType];
        bool mineable=t.HasTile&&natural&&!Falling(t.TileType)&&ToolSelector.CanMineTile(pick,t.TileType)&&pick!=null&&pick.pick>=(TileLoader.GetTile(t.TileType)?.MinPick??0);
        bool protect=Main.wallHouse[t.WallType]||t.HasActuator||t.IsActuated||t.TileColor!=PaintID.None||t.WallColor!=PaintID.None||
            t.HasTile&&Main.tileFrameImportant[t.TileType]&&!Main.tileCut[t.TileType];
        return new(x,y,t.HasTile?t.TileType:-1,mineable,protect,Hazard(t)!=null||t.HasTile&&Falling(t.TileType));
    }
}
