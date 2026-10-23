// TerrarAI replacement implementation, 2026-10-20. See docs/source-replacement.md.
using System;
using System.Linq;
using Microsoft.Xna.Framework;
namespace TerrarAI.Content.Actions;
public sealed class MineAction : TileTargetAction
{
    private readonly Point[] _cells;
    private readonly bool _inventoryOnly;
    private readonly Func<Point, bool>? _guard;
    private int _cursor;
    private TileWork? _worker;
    public MineAction(Point tile, bool singleTile = false, bool inventoryToolsOnly = false, Func<Point, bool>? mayMine = null) : base(tile)
    {
        _inventoryOnly = inventoryToolsOnly; _guard = mayMine;
        _cells = singleTile ? new[] { tile } : Enumerable.Range(-1, 3).SelectMany(y => Enumerable.Range(-1, 3)
            .Select(x => new Point(tile.X + x, tile.Y + y)))
            .OrderBy(p => Math.Abs(p.X - tile.X) + Math.Abs(p.Y - tile.Y)).ThenBy(p => p.Y).ThenBy(p => p.X).ToArray();
    }
    public override string Name => "mine";
    protected override int MaxExecutionTicks => 1800;
    public override float GetRequiredRange() => 80;
    public override Point? GetTargetTile() => _cells[Math.Min(_cursor, _cells.Length - 1)];
    protected override AgentActionResult OnTick(AgentActionContext context)
    {
        _worker ??= new(_cells[_cursor], false, _inventoryOnly, _guard);
        var result = _worker.Tick(context, RecordMutation);
        if (result.Status != AgentActionStatus.Success) return result;
        _worker.Cancel(); _worker = null;
        return ++_cursor == _cells.Length ? AgentActionResult.Success("采集区域已逐格检查，掉落需另行核对。")
            : AgentActionResult.Pending("继续检查采集区域下一格。");
    }
    protected override void OnCancel() { _worker?.Cancel(); }
    public override void Reset() { base.Reset(); _worker = null; _cursor = 0; }
}
