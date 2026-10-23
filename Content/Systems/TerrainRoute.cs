using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerrarAI.Content.Actions;
using TerrarAI.Content.NPCs;

namespace TerrarAI.Content.Systems;

// Bounded platformer graph: nodes are safe standing positions, edges are walks,
// falls or collision-tested jumps. Planning never edits tiles or moves entities.
internal sealed class TerrainRoute
{
    internal enum Travel { Ground, Swim, Door, Drop, Climb }
    internal sealed record Leg(Point From, Point To, float Vx, float Jump, int Frames, Travel Kind = Travel.Ground, Point? Door = null);
    internal sealed record Plan(List<Leg> Legs, int Expanded);
    private readonly Vector2 _target;
    private readonly float _tolerance, _speed;
    private readonly HashSet<(Point, Point, float, float)> _blocked = new();
    private List<Leg>? _legs;
    private int _cursor, _frames, _legTicks, _replans, _wait;
    private bool _launched, _airborne;
    private string? _error;
    internal TerrainRoute(Vector2 target, float tolerance, float speed)
    { _target = target; _tolerance = tolerance; _speed = speed; }

    private static Vector2 Position(Point p, int w, int h) => new(p.X * 16 + 8 - w / 2f, p.Y * 16 - h);
    private static Point Node(Vector2 pos, int w, int h) => new((int)MathF.Round((pos.X + w / 2f - 8) / 16), (int)MathF.Round((pos.Y + h) / 16));
    internal static bool InWater(Vector2 pos, int w, int h)
    {
        for (int x = (int)(pos.X / 16); x <= (int)((pos.X + w - 1) / 16); x++)
            for (int y = (int)(pos.Y / 16); y <= (int)((pos.Y + h - .1f) / 16); y++)
            {
                var t = Framing.GetTileSafely(x, y);
                if (t.LiquidType == LiquidID.Water && t.LiquidAmount > 64 &&
                    pos.Y + h > (y + 1) * 16 - t.LiquidAmount / 255f * 16 + .1f) return true;
            }
        return false;
    }
    private static bool Aquatic(Vector2 pos, int w, int h) => InWater(pos, w, h) || InWater(pos + new Vector2(0, 16), w, h);
    private static bool Safe(Vector2 pos, int w, int h, Point? door = null)
    {
        int left = (int)(pos.X / 16), top = (int)(pos.Y / 16);
        int right = (int)((pos.X + w - 1) / 16), bottom = (int)((pos.Y + h - 1) / 16);
        if (left < 10 || top < 10 || right >= Main.maxTilesX - 10 || bottom >= Main.maxTilesY - 10) return false;
        if (door == null && Collision.SolidCollision(pos, w, h)) return false;
        for (int x = left; x <= right; x++) for (int y = top; y <= bottom + 1; y++)
        {
            var t = Framing.GetTileSafely(x, y);
            if (t.LiquidAmount > 64 && t.LiquidType != LiquidID.Water || t.HasTile && !t.IsActuated && (t.TileType == TileID.Spikes || t.TileType == TileID.WoodenSpikes)) return false;
            if (door != null && y <= bottom && t.HasTile && !t.IsActuated && Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType] &&
                !(x == door.Value.X && Math.Abs(y - door.Value.Y) <= 1 && t.TileType == TileID.ClosedDoor && !WorldGen.IsLockedDoor(t))) return false;
        }
        return true;
    }
    private static bool ClearSegment(Vector2 from, Vector2 to, int w, int h)
    {
        int steps = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(from, to) / 4));
        var delta = (to - from) / steps;
        for (int n = 0; n < steps; n++)
        {
            var motion = Collision.TileCollision(from, delta, w, h);
            if (Vector2.DistanceSquared(motion, delta) > .01f) return false;
            from += delta;
            if (!Safe(from, w, h)) return false;
        }
        return true;
    }
    private static bool Navigable(Point p, int w, int h)
    { var pos = Position(p, w, h); return Safe(pos, w, h) && (Aquatic(pos, w, h) || Stand(p, w, h)); }

    private static Leg? DoorLeg(Point from, int direction, int w, int h, float speed)
    {
        for (int step = 1; step <= 3; step++)
        {
            int x = from.X + direction * step, y = from.Y - 2;
            var tile = Framing.GetTileSafely(x, y);
            if (!tile.HasTile || tile.IsActuated || tile.TileType != TileID.ClosedDoor || WorldGen.IsLockedDoor(tile)) continue;
            var door = new Point(x, y + 1 - tile.TileFrameY % 54 / 18);
            if (door.Y != from.Y - 2) return null;
            var to = new Point(x + direction * 2, from.Y);
            if (!Stand(to, w, h)) return null;
            var start = Position(from, w, h); var end = Position(to, w, h);
            for (int n = 0, count = (int)(Math.Abs(end.X - start.X) / 4); n <= count; n++)
            {
                var pos = Vector2.Lerp(start, end, n / (float)count);
                if (!Safe(pos, w, h, door)) return null;
                bool floor = false;
                for (int xx = (int)(pos.X / 16); xx <= (int)((pos.X + w - 1) / 16); xx++)
                { var t = Framing.GetTileSafely(xx, from.Y); floor |= t.HasTile && !t.IsActuated && Main.tileSolid[t.TileType]; }
                if (!floor) return null;
            }
            return new(from, to, direction * speed, 0, (int)MathF.Ceiling(Math.Abs(end.X - start.X) / speed) + 20, Travel.Door, door);
        }
        return null;
    }
    private static bool Stand(Point p, int w, int h)
    {
        var pos = Position(p, w, h);
        return Safe(pos, w, h) && Collision.TileCollision(pos, new Vector2(0, 1), w, h).Y < 0.5f;
    }
    // Short steps need braking or a vertical hop in a narrow corridor. A
    // constant horizontal ballistic arc cannot land inside a three-tile tread.
    private static Leg? StepLeg(Point from,Point to,int w,int h,float speed)
    {
        if(Math.Abs(to.X-from.X)>4||Math.Abs(to.Y-from.Y)!=1||!Stand(from,w,h)||!Stand(to,w,h))return null;
        var a=Position(from,w,h);var b=Position(to,w,h);
        if(InWater(a,w,h)||InWater(b,w,h))return null;
        bool down=to.Y>from.Y;
        var corner=down?new Vector2(b.X,a.Y):new Vector2(a.X,b.Y);
        if(!ClearSegment(a,corner,w,h)||!ClearSegment(corner,b,w,h))return null;
        var horizontalStart=down?a:corner;var horizontalEnd=down?corner:b;
        int count=Math.Max(1,(int)MathF.Ceiling(Math.Abs(b.X-a.X)/4));
        for(int i=0;i<=count;i++)
        {
            var p=Vector2.Lerp(horizontalStart,horizontalEnd,i/(float)count);
            var fall=Collision.TileCollision(p,new Vector2(0,17),w,h);
            // A failed hop must still land within one tile. Never turn a
            // short step edge into an unverified jump over a deep gap.
            if(fall.Y>16.1f||!Safe(p+fall,w,h))return null;
        }
        if(!down&&!ClearSegment(a,a-new Vector2(0,20),w,h))return null;
        return new(from,to,Math.Sign(to.X-from.X)*speed,down?0:-3.5f,
            (int)MathF.Ceiling(Math.Abs(b.X-a.X)/speed)+40,down?Travel.Drop:Travel.Climb);
    }
    private static Leg? Arc(Point from, float vx, float jump, int w, int h)
    {
        var pos = Position(from, w, h); float vy = jump; bool airborne = jump < 0;
        for (int frame = 1; frame <= 90; frame++)
        {
            vy = Math.Min(10, vy + 0.3f);
            var motion = Collision.TileCollision(pos, new Vector2(vx, vy), w, h);
            if (Math.Abs(motion.X - vx) > 0.05f || vy < 0 && Math.Abs(motion.Y - vy) > 0.05f) return null;
            pos += motion;
            if (!Safe(pos, w, h) || InWater(pos, w, h) || pos.Y - Position(from, w, h).Y > 12 * 16) return null;
            if (motion.Y > 0.5f) airborne = true;
            if (vy > 0 && motion.Y < vy - 0.05f)
            {
                if (airborne && frame > 2)
                {
                    var to = Node(pos, w, h); var end = Position(to, w, h);
                    if (to == from || !Stand(to, w, h)) return null;
                    var correction = end - pos;
                    if (Math.Abs(correction.Y) > 1 || Collision.TileCollision(pos, correction, w, h) != correction) return null;
                    return new(from, to, vx, jump, frame);
                }
                // Walking off a ledge may take several frames for a wide body.
                if (jump < 0 || frame > 16) return null;
                vy = 0;
            }
        }
        return null;
    }

    internal static Plan? Find(NPC npc, Vector2 target, float tolerance, float speed,
        HashSet<(Point, Point, float, float)>? blocked = null)
    {
        int w = npc.width, h = npc.height;
        bool wetGoal = InWater(target - new Vector2(w / 2f, h / 2f), w, h);
        var start = Node(npc.position, w, h);
        // Align to a nearby standing node without teleporting: the controller
        // walks to the first launch position before executing the plan.
        if (!Navigable(start, w, h)) return null;
        var goal = Node(target - new Vector2(w / 2f, h / 2f), w, h);
        int left = Math.Min(start.X, goal.X) - 24, right = Math.Max(start.X, goal.X) + 24;
        int top = Math.Min(start.Y, goal.Y) - 20, bottom = Math.Max(start.Y, goal.Y) + 16;
        if (right - left > 160 || bottom - top > 96) return null;
        float jumpScale = npc.ModNPC is AIAgentNPC a ? Math.Clamp(a.GetJumpMultiplier(), 0.5f, 1.5f) : 1;
        var queue = new PriorityQueue<Point, float>();
        var costs = new Dictionary<Point, float> { [start] = 0 };
        var parents = new Dictionary<Point, Leg>();
        var closed = new HashSet<Point>();
        float Heuristic(Point p) => Vector2.Distance(Position(p, w, h) + new Vector2(w / 2f, h / 2f), target) / speed;
        queue.Enqueue(start, Heuristic(start));
        while (queue.TryDequeue(out var at, out _) && closed.Count < 1800)
        {
            if (!closed.Add(at)) continue;
            if ((wetGoal || Stand(at, w, h) && !InWater(Position(at, w, h), w, h)) &&
                Vector2.Distance(Position(at, w, h) + new Vector2(w / 2f, h / 2f), target) <= tolerance)
            {
                var result = new List<Leg>();
                while (parents.TryGetValue(at, out var leg)) { result.Add(leg); at = leg.From; }
                result.Reverse(); return new(result, closed.Count);
            }
            void Add(Leg? leg)
            {
                if (leg == null || leg.To.X < left || leg.To.X > right || leg.To.Y < top || leg.To.Y > bottom ||
                    blocked?.Contains((leg.From, leg.To, leg.Vx, leg.Jump)) == true) return;
                float cost = costs[at] + leg.Frames + (leg.Jump == 0 ? 0 : 4);
                if (costs.TryGetValue(leg.To, out float previous) && cost >= previous) return;
                costs[leg.To] = cost; parents[leg.To] = leg;
                queue.Enqueue(leg.To, cost + Heuristic(leg.To));
            }
            var here = Position(at, w, h); bool aquatic = Aquatic(here, w, h);
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                var next = new Point(at.X + dx, at.Y + dy); var pos = Position(next, w, h);
                if ((aquatic || Aquatic(pos, w, h)) && Navigable(next, w, h) && ClearSegment(here, pos, w, h))
                    Add(new(at, next, 0, 0, 24, Travel.Swim));
            }
            // Water in the tile below provides swim connectivity, but does not
            // remove the floor. A supported dry rim still needs walk/jump edges
            // to clear a raised bank; the swim motor cannot rise while dry.
            bool standing = Stand(at, w, h);
            if (standing) foreach (int direction in new[] { -1, 1 }) Add(DoorLeg(at, direction, w, h, speed));
            if (!standing) continue;
            foreach(int direction in new[]{-1,1})for(int step=1;step<=4;step++)
                foreach(int dy in new[]{-1,1})Add(StepLeg(at,new Point(at.X+direction*step,at.Y+dy),w,h,speed));
            foreach (int direction in new[] { -1, 1 })
            {
                var next = new Point(at.X + direction, at.Y);
                var pos = Position(at, w, h); var delta = new Vector2(direction * 16, 0);
                if (Stand(next, w, h) && Collision.TileCollision(pos, delta, w, h) == delta)
                    Add(new(at, next, direction * speed, 0, (int)MathF.Ceiling(16 / speed)));
                else Add(Arc(at, direction * speed, 0, w, h));
                foreach (float vx in new[] { direction * speed, direction * speed / 2 })
                    foreach (float jump in new[] { -6.5f * jumpScale, -8f * jumpScale }) Add(Arc(at, vx, jump, w, h));
            }
            Add(Arc(at, 0, -8f * jumpScale, w, h));
        }
        return null;
    }

    private AgentActionResult Fail(NPC npc, string reason)
    { MovementHelper.Stop(npc); _error = reason; return AgentActionResult.Failure(reason); }

    internal AgentActionResult Tick(NPC npc)
    {
        if (_error != null) return Fail(npc, _error);
        if (++_frames > 900) return Fail(npc, "寻路超过 15 秒，已停止；目标可能无法到达。");
        bool grounded = MovementHelper.IsOnGround(npc) && npc.velocity.Y >= 0;
        bool swimming = Aquatic(npc.position, npc.width, npc.height) && Safe(npc.position, npc.width, npc.height);
        bool wetGoal = InWater(_target - new Vector2(npc.width / 2f, npc.height / 2f), npc.width, npc.height);
        if ((wetGoal ? grounded || swimming : grounded && !MovementHelper.InWater(npc)) && Vector2.Distance(npc.Center, _target) <= _tolerance)
        { MovementHelper.Hold(npc); return AgentActionResult.Success("已到达目标附近。"); }
        if (_legs == null)
        {
            if (!grounded && !swimming)
            {
                npc.velocity.X = 0;
                return ++_wait < 90 ? AgentActionResult.Pending("等待落地后规划路线……") : Fail(npc, "没有安全落脚点，无法规划路线。");
            }
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var plan = Find(npc, _target, _tolerance, _speed, _blocked);
            timer.Stop();
            CompanionMemorySystem.Record("navigation_route", null, null, new { fromX = npc.Center.X, fromY = npc.Center.Y,
                targetX = _target.X, targetY = _target.Y, found = plan != null, nodes = plan?.Legs.Count,
                expanded = plan?.Expanded, elapsedMs = timer.Elapsed.TotalMilliseconds, replans = _replans });
            if (plan == null) return Fail(npc, "有限范围内未找到安全绕行路线，已停止。");
            _legs = plan.Legs; _cursor = _legTicks = 0; _launched = _airborne = false;
        }
        if (_legs.Count == 0)
        {
            // The nearest standing node may be up to eight pixels closer to
            // the goal than the actual body. Walk that alignment explicitly;
            // treating an empty plan as a bad landing loops without moving.
            var node = Node(npc.position, npc.width, npc.height);
            var align = Position(node, npc.width, npc.height) - npc.position;
            var horizontal = new Vector2(align.X, 0);
            if (MovementHelper.InWater(npc) && align.LengthSquared() > .01f && Safe(Position(node, npc.width, npc.height), npc.width, npc.height))
            { MovementHelper.Swim(npc, align, Math.Min(_speed, 2.5f)); return AgentActionResult.Pending("水中靠近目标……"); }
            if (grounded && Math.Abs(align.X) > .1f && Math.Abs(align.Y) < 4 &&
                Stand(node, npc.width, npc.height) && Collision.TileCollision(npc.position, horizontal, npc.width, npc.height) == horizontal)
            {
                MovementHelper.Steer(npc, align.X, _speed);
                return AgentActionResult.Pending("靠近目标落脚点……");
            }
        }
        if (_cursor >= _legs.Count) return Replan(npc, "落点与目标存在偏差");
        var leg = _legs[_cursor];
        var from = Position(leg.From, npc.width, npc.height) + new Vector2(npc.width / 2f, npc.height);
        var to = Position(leg.To, npc.width, npc.height) + new Vector2(npc.width / 2f, npc.height);
        if (++_legTicks > leg.Frames + 70) return Replan(npc, "路线被阻挡或地形变化");
        if (leg.Kind == Travel.Swim)
        {
            if (!Navigable(leg.To, npc.width, npc.height) || !Safe(npc.position, npc.width, npc.height)) return Replan(npc, "水路变化或出现危险液体");
            var delta = to - npc.Bottom;
            if (Math.Abs(delta.X) < 3 && Math.Abs(delta.Y) < 4) Advance(npc);
            else
            {
                MovementHelper.Swim(npc, delta, Math.Min(_speed, 2.5f));
                // A tiny remaining foot overlap otherwise converges on the
                // shoreline asymptotically while X remains blocked by its wall.
                // Push off while touching water; native gravity lands on shore.
                if (npc.collideX && MovementHelper.InWater(npc) && Math.Abs(delta.X) > 2 && Math.Abs(delta.Y) < 16 &&
                    Stand(leg.To, npc.width, npc.height) && !InWater(Position(leg.To, npc.width, npc.height), npc.width, npc.height))
                    npc.velocity.Y = -4.5f - (npc.noGravity ? 0 : npc.gravity);
            }
        }
        else if(leg.Kind is Travel.Drop or Travel.Climb)
        {
            if(StepLeg(leg.From,leg.To,npc.width,npc.height,_speed)==null||!Safe(npc.position,npc.width,npc.height))
                return Replan(npc,"台阶、落脚点或头部空间变化");
            if(leg.Kind==Travel.Climb&&!_launched)
            {
                if(!grounded)return AgentActionResult.Pending("等待站稳后上台阶……");
                // Align the launch before the vertical hop, using the existing motor.
                if(Math.Abs(npc.Bottom.X-from.X)>2)MovementHelper.Steer(npc,from.X-npc.Bottom.X,_speed);
                else {_launched=MovementHelper.BeginJump(npc,0,-3.5f);_legTicks=0;}
            }
            else
            {
                if(!grounded)_airborne=true;
                bool rising=leg.Kind==Travel.Climb&&npc.Bottom.Y>to.Y+.1f&&!(_airborne&&grounded);
                MovementHelper.Steer(npc,rising?0:to.X-npc.Bottom.X,_speed);
                if(Math.Abs(npc.Bottom.X-to.X)<2&&Math.Abs(npc.Bottom.Y-to.Y)<2&&grounded)Advance(npc);
                else if(leg.Kind==Travel.Climb&&_airborne&&grounded&&npc.Bottom.Y>to.Y+2)
                    return Replan(npc,"上台阶未到达预期高度");
            }
        }
        else if (leg.Kind == Travel.Door)
        {
            var door = leg.Door!.Value;
            var tile = Framing.GetTileSafely(door.X, door.Y);
            if (tile.HasTile && tile.TileType == TileID.ClosedDoor)
            {
                if (Math.Abs(npc.Center.X - (door.X * 16 + 8)) > 64)
                { MovementHelper.Steer(npc, from.X - npc.Bottom.X, _speed); return AgentActionResult.Pending("走近门口……"); }
                bool opened = grounded && !WorldGen.IsLockedDoor(tile) &&
                    (WorldGen.OpenDoor(door.X, door.Y, Math.Sign(leg.Vx)) || WorldGen.OpenDoor(door.X, door.Y, -Math.Sign(leg.Vx)));
                CompanionMemorySystem.Record("navigation_door", null, null, new { door.X, door.Y, opened });
                if (!opened) return Replan(npc, "门已上锁或两侧被挡住");
                if (Main.netMode == NetmodeID.Server) NetMessage.SendTileSquare(-1, door.X, door.Y, 5);
            }
            if (!Stand(leg.To, npc.width, npc.height) || !ClearSegment(npc.position, Position(leg.To, npc.width, npc.height), npc.width, npc.height))
                return Replan(npc, "门口通道变化");
            if (Math.Abs(npc.Bottom.X - to.X) < 3) Advance(npc);
            else MovementHelper.Steer(npc, to.X - npc.Bottom.X, _speed);
        }
        else if (leg.Jump == 0 && leg.From.Y == leg.To.Y)
        {
            if (!Stand(leg.To, npc.width, npc.height)) return Replan(npc, "落脚点变化");
            if (Math.Abs(npc.Bottom.X - to.X) < 3 && Math.Abs(npc.Bottom.Y - to.Y) < 4) Advance(npc);
            else MovementHelper.Steer(npc, to.X - npc.Bottom.X, _speed);
        }
        else if (!_launched)
        {
            if (!grounded) return AgentActionResult.Pending("等待安全起跳……");
            if (Math.Abs(npc.Bottom.X - from.X) > 2)
                MovementHelper.Steer(npc, from.X - npc.Bottom.X, _speed);
            else
            {
                var verified = Arc(leg.From, leg.Vx, leg.Jump, npc.width, npc.height);
                if (verified?.To != leg.To) return Replan(npc, "跳跃通道变化");
                _launched = true; _airborne = false; _legTicks = 0;
                if (leg.Jump < 0) MovementHelper.BeginJump(npc, leg.Vx, leg.Jump);
                else npc.velocity.X = leg.Vx;
            }
        }
        else
        {
            // Native gravity/collision handles the trajectory; only steer X.
            // A fall starts by walking off a ledge. A 40px body can remain
            // supported for many ticks; that is not an unexpected landing.
            if (!grounded) _airborne = true;
            if (_legTicks > 2 && grounded && _airborne)
            {
                // A wide body may touch the edge of a bank before its centre
                // reaches the planned node. Finish that collision-tested walk;
                // advancing with the former 12px slack can strand the next
                // leg at an unsupported rounded node on the water's edge.
                var landing = Position(leg.To, npc.width, npc.height);
                if (Math.Abs(npc.Bottom.Y - to.Y) >= 4 || Math.Abs(npc.Bottom.X - to.X) >= 12 ||
                    !ClearSegment(npc.position, landing, npc.width, npc.height))
                    return Replan(npc, "实际跳跃落点变化");
                if (Math.Abs(npc.Bottom.X - to.X) < 2) Advance(npc);
                else MovementHelper.Steer(npc, to.X - npc.Bottom.X, _speed);
            }
            else npc.velocity.X = Math.Abs(npc.Bottom.X - to.X) < Math.Abs(leg.Vx) ? to.X - npc.Bottom.X : leg.Vx;
        }
        return AgentActionResult.Pending($"绕行 {_cursor + 1}/{_legs.Count} · 目标 ({(int)(_target.X / 16)},{(int)(_target.Y / 16)})");
    }
    private void Advance(NPC npc) { _cursor++; _legTicks = 0; _launched = _airborne = false; MovementHelper.Hold(npc); }
    private AgentActionResult Replan(NPC npc, string reason)
    {
        MovementHelper.Stop(npc);
        CompanionMemorySystem.Record("navigation_replan", null, null, new { reason, fromX = npc.Center.X, fromY = npc.Center.Y,
            targetX = _target.X, targetY = _target.Y, cursor = _cursor, airborne = _airborne,
            wet = MovementHelper.InWater(npc), replans = _replans + 1 });
        if (_legs != null && _cursor < _legs.Count)
        { var l = _legs[_cursor]; _blocked.Add((l.From, l.To, l.Vx, l.Jump)); }
        if (++_replans > 3) return Fail(npc, reason + "，已达到重算上限。");
        _legs = null; _wait = 0;
        return AgentActionResult.Pending(reason + "，重新寻路……");
    }
}
