using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using TerrarAI.Content.Companion;
using Terraria;
using Terraria.Enums;
using Terraria.ID;

namespace TerrarAI.Content.Systems;

internal sealed record HousingInspection(Point Seed, Rectangle Bounds, bool Suitable, bool Enclosed,
    bool Light, bool Table, bool Chair, bool Door, bool Occupied, string Fingerprint,
    IReadOnlyList<string> Issues, StructurePlan Plan)
{
    internal string Summary => Suitable ? "原生检测：房屋合格，可供普通城镇 NPC 入住。"
        : "原生检测未通过：" + string.Join("；", Issues);
}

// Game-thread adapter. Uses the same native checks as the housing UI, without assigning
// NPCs, spawning entities, moving the player or changing tiles/inventory.
internal static class HousingInspector
{
    private static bool Flag(string name) => (bool)(typeof(WorldGen).GetField(name,
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null)
        ?? throw new InvalidOperationException("房屋检测接口不可用：" + name));

    internal static HousingInspection Scan(Player player, Point seed)
    {
        if (!WorldGen.InWorld(seed.X, seed.Y, 12) || Vector2.Distance(player.Center, seed.ToWorldCoordinates()) > 40 * 16)
            throw new ArgumentException("请点击距离角色40格内、远离世界边缘的房屋内部。 ");
        bool enclosed = WorldGen.StartRoomCheck(seed.X, seed.Y);
        var failure = WorldGen.roomCheckFailureReason;
        bool needs = enclosed && WorldGen.RoomNeeds(NPCID.Guide);
        bool light = false, table = false, chair = false, door = false, occupied = false;
        int score = 0;
        var issues = new List<string>();
        Rectangle bounds;
        if (enclosed)
            bounds = new(WorldGen.roomX1, WorldGen.roomY1, WorldGen.roomX2 - WorldGen.roomX1 + 1, WorldGen.roomY2 - WorldGen.roomY1 + 1);
        else
        {
            bounds = RectangularShell(seed) ?? new Rectangle(seed.X, seed.Y, 1, 1);
            issues.Add(failure switch
            {
                TownNPCRoomCheckFailureReason.RoomIsTooSmall => "空间太小，原生房屋面积检查未通过",
                TownNPCRoomCheckFailureReason.RoomIsTooBig => "房间过大或边界未封闭，请检查墙体和屋顶",
                TownNPCRoomCheckFailureReason.HoleInWallIsTooBig => "背景墙缺口过大",
                TownNPCRoomCheckFailureReason.RoomCheckStartedInASolidTile => "选中了实心方块，请点击屋内空格",
                TownNPCRoomCheckFailureReason.TooCloseToWorldEdge => "房屋太靠近世界边缘",
                _ => "未形成可居住房间，请检查墙体、屋顶和背景墙"
            });
        }
        if (bounds.Width > StructurePlan.MaxWidth || bounds.Height > StructurePlan.MaxHeight)
            throw new ArgumentException("房间超过120×80格，请选择较小的单间。 ");
        bool rectangle = bounds.Width >= 4 && bounds.Height >= 4 && Shell(bounds);
        if (enclosed)
        {
            light = Flag("roomTorch"); table = Flag("roomTable"); chair = Flag("roomChair"); door = Flag("roomDoor");
            WorldGen.ScoreRoom(-1, NPCID.Guide);
            score = WorldGen.hiScore; occupied = Flag("roomOccupied");
            if (occupied) issues.Add("房间已被其他 NPC 占用，不能再分配");
            else if (Flag("roomEvil")) issues.Add("周围邪恶环境不适合居住");
            else if (score <= 0) issues.Add("缺少合适的入住落脚点，检查实心地板及其上方空间");
            if (Flag("roomHasStinkbug") || Flag("roomHasEchoStinkbug")) issues.Add("房间里有阻止入住的臭虫");
        }
        else if (rectangle)
        {
            // Native flood-fill may stop at a missing background wall. Only use these
            // supplemental hints inside a completely verified rectangular shell.
            bool Has(int[] types) => Cells(bounds).Any(p =>
            {
                var t = Framing.GetTileSafely(p.X, p.Y); return t.HasTile && !t.IsActuated && types.Contains(t.TileType);
            });
            light = Has(TileID.Sets.RoomNeeds.CountsAsTorch); table = Has(TileID.Sets.RoomNeeds.CountsAsTable);
            chair = Has(TileID.Sets.RoomNeeds.CountsAsChair); door = Has(TileID.Sets.RoomNeeds.CountsAsDoor);
        }
        if (enclosed || rectangle)
        {
            if (!light) issues.Add("缺光源"); if (!table) issues.Add("缺桌面（桌子或工作台）");
            if (!chair) issues.Add("缺椅子类家具"); if (!door) issues.Add("缺入口（门或平台）；请手动设置，不会拆墙开门");
        }
        bool suitable = enclosed && needs && score > 0 && issues.Count == 0;
        var steps = new List<StructureStep>();
        // Invalid space/open boundary and occupied rooms are diagnostic-only. A wall
        // failure can be filled only after independently verifying the whole shell.
        bool repairable = !suitable && !occupied && (enclosed || rectangle && failure == TownNPCRoomCheckFailureReason.HoleInWallIsTooBig);
        if (repairable)
        {
            var room = enclosed ? Enumerable.Range(0, WorldGen.numRoomTiles)
                .Select(i => new Point(WorldGen.roomX[i], WorldGen.roomY[i])).ToHashSet() : Cells(bounds).ToHashSet();
            PlanRepairs(player, bounds, rectangle, light, table, chair, steps, issues, room);
        }
        var plan = new StructurePlan("房屋修补", "原生房屋检测", bounds.Width, bounds.Height, steps);
        return new(seed, bounds, suitable, enclosed, light, table, chair, door, occupied,
            Fingerprint(bounds, suitable, issues), issues.AsReadOnly(), plan);
    }

    private static void PlanRepairs(Player player, Rectangle bounds, bool rectangle, bool light,
        bool table, bool chair, List<StructureStep> steps, List<string> issues, HashSet<Point> room)
    {
        var reserved = new HashSet<(int X, int Y)>();
        if (rectangle)
        {
            int missingWalls = 0;
            for (int y = bounds.Top + 1; y < bounds.Bottom - 1; y++)
                for (int x = bounds.Left + 1; x < bounds.Right - 1; x++)
                {
                    var t = Framing.GetTileSafely(x, y);
                    if (t.WallType != WallID.None || Hazard(t)) continue;
                    steps.Add(new(x - bounds.Left, y - bounds.Top, StepKind.Wall, WallID.Wood, 0, ItemID.WoodWall)); missingWalls++;
                }
            if (missingWalls > 0) issues.Add($"背景墙缺口 {missingWalls} 格，可补木墙");
        }
        var palette = StructurePresets.NpcRooms(1, StructureLibrary.WoodPalette);
        var chairStep = HousingRepairLayout.Furniture(palette, TileID.Chairs);
        var tableStep = HousingRepairLayout.Furniture(palette, TileID.Tables);
        var torchStep = HousingRepairLayout.Furniture(palette, TileID.Torches);
        player.AdjTiles();
        bool missingCraft(int item) => Count(player, item) == 0 && RepairCrafting.RecipeFor(item)?.Workbench == true;
        bool needsBench = !player.adjTile[TileID.WorkBenches] &&
            ((!chair && missingCraft(ItemID.WoodenChair)) || (!table && missingCraft(ItemID.WoodenTable)) ||
             (steps.Count(s => s.Kind == StepKind.Wall) > Count(player, ItemID.WoodWall)));
        bool AddFloor(StructureStep template, bool nearPlayer = false)
        {
            var step = HousingRepairLayout.Floor(bounds.Width, bounds.Height, template,
                (x, y) => room.Contains(new(x + bounds.Left, y + bounds.Top)) && !Framing.GetTileSafely(x + bounds.Left, y + bounds.Top).HasTile
                    && !Hazard(Framing.GetTileSafely(x + bounds.Left, y + bounds.Top)),
                (x, y) => WorldGen.SolidTile(x + bounds.Left, y + bounds.Top), reserved,
                (x, y) => Vector2.DistanceSquared(player.Center, new Point(x + bounds.Left, y + bounds.Top).ToWorldCoordinates()),
                (x, y) => !nearPlayer || (Math.Abs(x + bounds.Left - player.Center.X / 16) <= 2 && Math.Abs(y + bounds.Top - player.Center.Y / 16) <= 2));
            if (step == null) return false;
            steps.Add(step); foreach (var p in step.Footprint()) reserved.Add(p); return true;
        }
        if (needsBench)
        {
            var bench = new StructureStep(0, 0, StepKind.Furniture, TileID.WorkBenches, 0, ItemID.WorkBench, Width: 2);
            if (AddFloor(bench, true)) table = true; // A real workbench also satisfies the native table requirement.
            else issues.Add("合成需要工作台；请带齐成品，或站到屋内可放工作台的位置后重查");
        }
        if (!table && !AddFloor(tableStep)) issues.Add("没有可放桌子的空地或支撑，不会拆改现有家具");
        if (!chair && !AddFloor(chairStep)) issues.Add("没有可放椅子的空地或支撑，不会拆改现有家具");
        if (!light)
        {
            bool placed = false;
            for (int y = bounds.Top + 1; y < bounds.Bottom - 1 && !placed; y++) for (int x = bounds.Left + 1; x < bounds.Right - 1 && !placed; x++)
                {
                    var p = new Point(x, y); var t = Framing.GetTileSafely(x, y);
                    bool wall = Main.wallHouse[t.WallType] || steps.Any(s => s.Kind == StepKind.Wall && s.Dx == x - bounds.Left && s.Dy == y - bounds.Top);
                    if (room.Contains(p) && !t.HasTile && !Hazard(t) && wall && !reserved.Contains((x - bounds.Left, y - bounds.Top)))
                    { steps.Add(torchStep with { Dx = x - bounds.Left, Dy = y - bounds.Top }); placed = true; }
                }
            if (!placed) issues.Add("没有安全的火把附着点，请先补背景墙");
        }
    }

    internal static int Count(Player p, int item) => p.inventory.Take(50).Where(i => i.type == item).Sum(i => i.stack);
    internal static bool Hazard(Tile t) => t.LiquidAmount > 0 || t.RedWire || t.BlueWire || t.GreenWire || t.YellowWire || t.HasActuator || t.IsActuated;
    private static IEnumerable<Point> Cells(Rectangle r)
    { for (int x = r.Left; x < r.Right; x++) for (int y = r.Top; y < r.Bottom; y++) yield return new(x, y); }
    private static bool Boundary(int x, int y)
    { var t = Framing.GetTileSafely(x, y); return t.HasTile && !t.IsActuated && (WorldGen.SolidTile(x, y) || t.TileType is TileID.ClosedDoor or TileID.OpenDoor); }
    private static bool Shell(Rectangle r) =>
        Enumerable.Range(r.Left, r.Width).All(x => Boundary(x, r.Top) && Boundary(x, r.Bottom - 1)) &&
        Enumerable.Range(r.Top, r.Height).All(y => Boundary(r.Left, y) && Boundary(r.Right - 1, y));
    private static Rectangle? RectangularShell(Point seed)
    {
        int Ray(int dx, int dy, int max) { for (int i = 1; i <= max; i++) if (Boundary(seed.X + dx * i, seed.Y + dy * i)) return i; return 0; }
        int l = Ray(-1, 0, 60), r = Ray(1, 0, 60), u = Ray(0, -1, 40), d = Ray(0, 1, 40);
        if (l * r * u * d == 0) return null;
        var b = new Rectangle(seed.X - l, seed.Y - u, l + r + 1, u + d + 1); return Shell(b) ? b : null;
    }
    private static string Fingerprint(Rectangle bounds, bool suitable, List<string> issues)
    {
        ulong hash = 14695981039346656037;
        void Add(ulong v) { hash ^= v; hash = unchecked(hash * 1099511628211); }
        foreach (var p in Cells(new(bounds.X - 1, bounds.Y - 1, bounds.Width + 2, bounds.Height + 2)))
        {
            var t = Framing.GetTileSafely(p.X, p.Y); Add(t.HasTile ? 1UL : 0); Add(t.TileType); Add(t.WallType);
            Add((ulong)(ushort)t.TileFrameX); Add((ulong)(ushort)t.TileFrameY); Add((ulong)t.Slope); Add(t.IsHalfBlock ? 1UL : 0);
            Add(t.LiquidAmount); Add((ulong)t.LiquidType); Add(Hazard(t) ? 1UL : 0); Add(t.TileColor); Add(t.WallColor);
        }
        return $"{bounds}:{hash:x16}:{suitable}:{string.Join('|', issues)}";
    }
}
