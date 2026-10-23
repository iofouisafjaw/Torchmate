// TerrarAI replacement implementation, 2026-10-20. See docs/source-replacement.md.
using System;
using Microsoft.Xna.Framework;
using Terraria;
using TerrarAI.Content.Systems;
namespace TerrarAI.Content.Actions;
public sealed class MoveAction : TileTargetAction
{
    private readonly float _reach, _speed;
    private MovementHelper.MovementState _route;
    private NPC? _actor;
    public MoveAction(Point targetTile, float tolerance = 1f, float speed = 3f) : base(targetTile)
    { _reach = Math.Clamp(tolerance, .25f, 5f) * 16; _speed = Math.Clamp(speed, 1, 6); }
    public Point TargetTile => Tile;
    public override string Name => "move";
    protected override int MaxExecutionTicks => 960;
    protected override AgentActionResult OnTick(AgentActionContext context)
    {
        _actor = context.Agent;
        if (!WorldGen.InWorld(Tile.X, Tile.Y, 2)) return AgentActionResult.Failure("移动目标越界。");
        if (TerrarAI_Config.Get().EnableCreativeMode)
        {
            _actor.Center = GetTileWorldCenter(); _actor.velocity = Vector2.Zero; _actor.netUpdate = true;
            return AgentActionResult.Success("作弊移动已到达指定坐标。");
        }
        return MovementHelper.MoveTowards(_actor, GetTileWorldCenter(), ref _route, MovementHelper.MovementSettings.Create(_speed, _reach));
    }
    protected override void OnCancel()
    { if (_actor != null) MovementHelper.Stop(_actor); _route = default; }
    public override void Reset() { base.Reset(); _route = default; _actor = null; }
}
