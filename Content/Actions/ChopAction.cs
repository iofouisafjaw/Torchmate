// TerrarAI replacement implementation, 2026-10-20. See docs/source-replacement.md.
using Microsoft.Xna.Framework;
namespace TerrarAI.Content.Actions;
public sealed class ChopAction(Point tile, bool inventoryToolsOnly = false) : TileTargetAction(tile)
{
    private TileWork? _worker;
    public override string Name => "chop";
    protected override int MaxExecutionTicks => 1200;
    public override float GetRequiredRange() => 32;
    protected override AgentActionResult OnTick(AgentActionContext context)
    {
        _worker ??= new(Tile, true, inventoryToolsOnly);
        return _worker.Tick(context, RecordMutation);
    }
    protected override void OnCancel() => _worker?.Cancel();
    public override void Reset() { base.Reset(); _worker = null; }
}
