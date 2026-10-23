// TerrarAI replacement implementation, 2026-10-20. See docs/source-replacement.md.
using Microsoft.Xna.Framework;
namespace TerrarAI.Content.Actions;
public abstract class TileTargetAction(Point tile) : AgentAction
{
    protected Point Tile { get; } = tile;
    public override Point? GetTargetTile() => Tile;
    protected Vector2 GetTileWorldCenter() => new(Tile.X * 16 + 8, Tile.Y * 16 + 8);
}
