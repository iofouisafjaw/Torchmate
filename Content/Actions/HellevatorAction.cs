// TerrarAI replacement implementation, 2026-10-20. See docs/source-replacement.md.
using System;
using Microsoft.Xna.Framework;
using TerrarAI.Content.Systems;
namespace TerrarAI.Content.Actions;

// Only an expedition's bounded, confirmed single-tile plan can construct this job.
public sealed class HellevatorAction : AgentAction
{
    private readonly int _column;
    private readonly Point _at;
    private readonly Func<Point, bool> _allowed;
    private MineAction? _work;
    internal HellevatorAction(int centerColumn, Point tile, Func<Point, bool> guard)
    { _column = centerColumn; _at = tile; _allowed = guard; }
    public override string Name => "hellevator";
    protected override int MaxExecutionTicks => 1200;
    protected override AgentActionResult OnTick(AgentActionContext context)
    {
        if (!_allowed(_at)) return AgentActionResult.Failure("竖井范围或保护条件变化，已停止。");
        float dx = _column * 16 + 8 - context.Agent.Center.X;
        if (Math.Abs(dx) > 32) return AgentActionResult.Failure("同伴已偏离竖井中心，未挖掘。");
        if (Math.Abs(dx) > 2)
        { MovementHelper.Steer(context.Agent, dx, 2); return AgentActionResult.Pending("对准竖井中心。"); }
        _work ??= new(_at, singleTile: true, inventoryToolsOnly: true, mayMine: _allowed);
        var step = _work.Tick(context);
        context.Agent.velocity.X = 0;
        if (step.Status != AgentActionStatus.Pending)
            foreach (var change in _work.Mutations) RecordMutation(change.X, change.Y, change.BeforeType, change.AfterType);
        return step;
    }
    protected override void OnCancel() => _work?.Cancel();
    public override void Reset() { base.Reset(); _work = null; }
}
