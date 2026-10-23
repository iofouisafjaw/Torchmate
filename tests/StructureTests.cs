using TerrarAI.Content.Companion;

static class StructureTests
{
    private static readonly StructurePresets.Palette Wood = new(30, 9, 4, 93, 10, 25, 14, 32, 15, 34, 4, 8);

    private sealed class Site(Func<StructureStep, CellState> inspect) : IStructureSite
    {
        public int Placed, Cleared;
        public CellState Inspect(StructureStep step) => inspect(step);
        public bool Clear(StructureStep step, out string error) { error = ""; Cleared++; return true; }
        public bool Place(StructureStep step, out string error) { error = ""; Placed++; return true; }
    }

    internal static void Run(Action<bool, string> check)
    {
        var house = StructurePresets.NpcRooms(1, Wood);
        var m = house.Materials();
        check(house.Width == 10 && house.Height == 6, "one NPC room is 10x6 outside (8x4 inside)");
        check(m[9] == 25 && m[93] == 32 && m[25] == 1 && m[32] == 1 && m[34] == 1 && m[8] == 1,
            "NPC room materials: 25 wood, 32 wood walls, door, table, chair, torch");
        check(house.Steps.First().Kind == StepKind.Block && house.Steps.First().Dy == 5 &&
              house.Steps.Last().Kind == StepKind.Furniture, "build order: floor first, furniture last");
        int firstWall = house.Steps.ToList().FindIndex(s => s.Kind == StepKind.Wall);
        check(house.Steps.Take(firstWall).All(s => s.Kind == StepKind.Block), "all blocks before background walls");
        var door = house.Steps.Single(s => s.Type == 10);
        check(door.Footprint().Select(p => p.Y).OrderBy(y => y).SequenceEqual(new[] { 2, 3, 4 }) && door.Footprint().All(p => p.X == 0),
            "door occupies the three wall tiles above the floor");
        var table = house.Steps.Single(s => s.Type == 14);
        check(table.Footprint().All(p => p.Y is 3 or 4 && p.X is >= 4 and <= 6), "table stands on the floor inside the room");
        var three = StructurePresets.NpcRooms(3, Wood);
        check(three.Width == 28 && three.Materials()[25] == 3 && three.Materials()[32] == 3, "three rooms share walls, one door each");

        var states = house.Steps.Select(s => s.Kind == StepKind.Wall ? CellState.Done : CellState.Empty).ToList();
        var missing = house.Missing(states, item => item == 9 ? 20 : 99);
        check(missing.Count == 1 && missing[9] == 5 && !missing.ContainsKey(93), "missing materials ignore cells already built");

        var site = new Site(s => s.Dy == 5 && s.Dx == 0 ? CellState.Clearable : s.Dy == 0 && s.Dx == 9 ? CellState.Blocked : CellState.Empty);
        var job = new StructureBuildJob(house);
        for (int i = 0; i < 500 && job.State == StructureJobState.Running; i++) job.Step(site);
        check(job.State == StructureJobState.Completed && job.Cleared == 1 && job.Failed == 1 && job.Placed == house.Steps.Count - 1,
            "job clears natural terrain, skips blocked cells without removing them, builds the rest");
        var empty = new StructureBuildJob(house);
        var broke = new FailingSite();
        empty.Step(broke);
        check(empty.State == StructureJobState.Failed && empty.Reason.StartsWith("材料不足"), "running out of materials stops the job");

        check(BuildRequest.Preset("帮我盖个NPC房子") == "preset:npc1" && BuildRequest.Preset("建两间房") == "preset:npc2"
              && BuildRequest.Preset("怎么盖房子？") == null && BuildRequest.Preset("砍树") == null, "build requests map to presets");
    }

    private sealed class FailingSite : IStructureSite
    {
        public CellState Inspect(StructureStep step) => CellState.Empty;
        public bool Clear(StructureStep step, out string error) { error = ""; return true; }
        public bool Place(StructureStep step, out string error) { error = "材料不足：木材"; return false; }
    }
}
