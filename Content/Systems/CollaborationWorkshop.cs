using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerrarAI.Content.Companion;

namespace TerrarAI.Content.Systems;

public sealed partial class StructureWorkshop
{
    internal static bool CollaborationPlayerBuild => CompanionCollaborationSystem.PlayerOwns(_task);
    internal static bool ArenaIncludesPotions => _arena?.Potions.Count > 0;
    internal static void CollaborationPreview(Point origin) => PreviewArena(origin);
    private static IStructureSite BuildSite(Player player, Point origin) =>
        CompanionCollaborationSystem.Current?.Next is { Id: "housing" } step && (step.TaskId == _task || CompanionCollaborationSystem.Preserving)
            ? new HousingStructureSite(player, origin) : new TerrariaStructureSite(player, origin);
    private static bool CollaborationFinalAudit(Player player, out string evidence)
    {
        evidence = "";
        if (CompanionCollaborationSystem.Current is not {} project || project.Next is not { Id: "housing" } step || step.TaskId != _task) return true;
        return CollaborationAudit(player, false, Origin, Plan, project.Rooms, out evidence);
    }
    internal static bool CollaborationAudit(Player player, bool arena, Point? origin, StructurePlan? house, int rooms, out string evidence)
    {
        evidence = "尚未选定原施工位置，请先打开预览选址。";
        if (origin is not {} at) return false;
        if (arena)
        {
            if (_arena == null) { evidence = "原竞技场布局已失效。"; return false; }
            var site = new ArenaFixtureSite(player, at);
            bool platforms = _arena.Platforms(new(at.X, at.Y), p => InspectArenaPlatform(_arena, p, site, at))
                .Audit(p => InspectArenaPlatform(_arena, p, site, at)).Passed;
            bool fixtures = _arena.Fixtures.Steps.All(s => site.Inspect(s) == CellState.Done);
            bool clear = ArenaClearForAudit(at, out string blocked);
            evidence = platforms && fixtures && clear ? "原位置平台、净空和设施实际核验通过" : "竞技场未达标：" + blocked;
            return platforms && fixtures && clear;
        }
        if (house == null) return false;
        var housing = new HousingStructureSite(player, at);
        if (!house.Steps.All(s => housing.Inspect(s) == CellState.Done)) { evidence = "原位置住房蓝图尚未完整达标"; return false; }
        for (int room = 0; room < rooms; room++)
        {
            var inspection = HousingInspector.Scan(player, new Point(at.X + 4 + room * 9, at.Y + 2));
            if (!inspection.Suitable) { evidence = $"第{room + 1}间：" + inspection.Summary; return false; }
        }
        evidence = $"原位置{rooms}间住房蓝图与原生可居住检测均通过"; return true;
    }
}
