using Microsoft.Xna.Framework;
using Terraria;
using TerrarAI.Content.Actions;

namespace TerrarAI.Content.Systems;

// A moving destination around the same bounded route executor used by gathering.
// Keep an airborne leg intact; replacing it mid-jump loses its landing state.
internal sealed class FollowNavigation
{
    private MovementHelper.MovementState _movement;
    private Vector2 _destination;
    private int _age, _retry;
    internal void Reset(NPC npc)
    {
        _movement.Reset(50); _age = _retry = 0;
        MovementHelper.Stop(npc);
    }

    internal string Tick(NPC npc, Player player, float speed)
    {
        _age++;
        bool grounded = MovementHelper.IsOnGround(npc) && npc.velocity.Y >= 0 || MovementHelper.InWater(npc);
        if (grounded && (!MovementHelper.InWater(npc) || player.wet) && Vector2.Distance(npc.Center, player.Center) <= 50)
        { Reset(npc); MovementHelper.Hold(npc); return "跟随中"; }

        bool moved = Vector2.DistanceSquared(player.Center, _destination) > 64 * 64;
        if (_movement.Route != null && moved && _age >= 30 && grounded)
        { _movement.Reset(50); _retry = 0; }
        if (_movement.Route == null)
        {
            // A failed static destination retries twice a second at most, and
            // normally once per two seconds, rather than planning every frame.
            if (_retry > 0 && !(moved && _age >= 30 && grounded))
            { _retry--; npc.velocity.X = 0; return "等待可走路线 · 可手动召回"; }
            _destination = player.Center; _age = 0; _retry = 0;
        }
        var result = MovementHelper.MoveTowards(npc, _destination, ref _movement,
            MovementHelper.MovementSettings.Create(speed, 50));
        if (result.Status == AgentActionStatus.Pending) return MovementHelper.InWater(npc) ? "水中跟随" : "绕路跟随";
        _movement.Reset(50); npc.velocity.X = 0;
        if (result.Status == AgentActionStatus.Failure)
        { _retry = 120; return "等待可走路线 · 可手动召回"; }
        return "跟随中";
    }
}
