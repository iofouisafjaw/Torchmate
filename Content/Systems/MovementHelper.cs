// TerrarAI replacement implementation, 2026-10-20. See docs/source-replacement.md.
using System;
using Microsoft.Xna.Framework;
using Terraria;
using TerrarAI.Content.Actions;
namespace TerrarAI.Content.Systems;

// Small native motor; terrain graph, doors and water routing stay in TerrainRoute.
internal static class MovementHelper
{
    internal readonly record struct MovementSettings(float Speed, float BaseTolerance)
    { public static MovementSettings Create(float speed, float tolerance) => new(speed, tolerance); }
    internal struct MovementState
    {
        internal TerrainRoute? Route;
        internal Vector2 Destination;
        internal MovementSettings Settings;
        public static MovementState Create(float baseTolerance) => default;
        public void Reset(float baseTolerance) => this = default;
    }
    public static AgentActionResult MoveTowards(NPC npc, Vector2 destination, ref MovementState state, MovementSettings settings)
    {
        if (!ServerAuthority.IsServer) return AgentActionResult.Failure("客户端不能控制移动。");
        if (state.Route == null || state.Destination != destination || state.Settings != settings)
        {
            state.Destination = destination; state.Settings = settings;
            state.Route = new TerrainRoute(destination, settings.BaseTolerance, settings.Speed);
        }
        return state.Route.Tick(npc);
    }
    internal static void Steer(NPC npc, float deltaX, float speed)
    {
        npc.velocity.X = Math.Clamp(deltaX, -Math.Abs(speed), Math.Abs(speed));
        if (Math.Abs(deltaX) >= .5f) npc.direction = deltaX < 0 ? -1 : 1;
    }
    internal static bool InWater(NPC npc) => TerrainRoute.InWater(npc.position, npc.width, npc.height);
    internal static void Swim(NPC npc, Vector2 delta, float speed)
    {
        Vector2 velocity = delta.LengthSquared() <= speed * speed ? delta : Vector2.Normalize(delta) * speed;
        Steer(npc, velocity.X, speed);
        if (InWater(npc)) npc.velocity.Y = velocity.Y - (npc.noGravity ? 0 : npc.gravity);
    }
    internal static void Hold(NPC npc)
    { npc.velocity.X = 0; if (InWater(npc)) Swim(npc, Vector2.Zero, 2.5f); }
    internal static void Stop(NPC npc)
    { npc.velocity.X = 0; if (InWater(npc)) npc.velocity.Y = 0; }
    internal static bool BeginJump(NPC npc, float vx, float vy)
    {
        bool ready = npc.velocity.Y >= 0 && IsOnGround(npc);
        if (ready) npc.velocity = new(vx, vy);
        return ready;
    }
    public static bool IsOnGround(NPC npc) => npc.collideY ||
        Collision.SolidCollision(new Vector2(npc.position.X, npc.Bottom.Y + 2), npc.width, 2);
    public static void ApplyFriction(NPC npc, float amount = 1) => npc.velocity.X *= 1 - .3f * Math.Clamp(amount, 0, 1);
    internal static Vector2? FindValidTeleportPosition(Player player, int offsetDistance = 64)
    {
        // Recall is explicit. Reject solid body space instead of falling back into a wall.
        foreach (int offset in new[] { -offsetDistance, offsetDistance, -2 * offsetDistance, 2 * offsetDistance })
        {
            var center = player.Center + new Vector2(offset, 0);
            if (!Collision.SolidCollision(center - new Vector2(20), 40, 40)) return center;
        }
        return null;
    }
}
