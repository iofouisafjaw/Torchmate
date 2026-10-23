// TerrarAI replacement implementation, 2026-10-20. See docs/source-replacement.md.
using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerrarAI.Content.Systems;
namespace TerrarAI.Content.Actions;

// Shared native work primitive for direct gathering, recipes and bounded shafts.
// It re-reads tools and the target before each mutation; it never grants permission.
internal sealed class TileWork(Point at, bool tree, bool inventoryOnly, Func<Point, bool>? guard = null)
{
    private MoveAction? _walk;
    private ushort? _observedType;
    private float _work;
    internal float Reach => tree ? 32 : 80;
    internal void Cancel() { _walk?.Cancel(); _walk = null; }
    internal AgentActionResult Tick(AgentActionContext context, Action<int, int, int?, int?> record)
    {
        if (!WorldGen.InWorld(at.X, at.Y, 2)) return AgentActionResult.Failure("采集坐标越界。");
        var cell = Framing.GetTileSafely(at.X, at.Y);
        if (!cell.HasTile) return AgentActionResult.Success("目标已为空；没有新增移除记录。");
        if (_observedType.HasValue && _observedType != cell.TileType) return AgentActionResult.Failure("目标类型变化，需重新核实。");
        _observedType ??= cell.TileType;
        if (tree && !TreeHelper.IsTreeTile(at)) return AgentActionResult.Failure("指定位置不是树干。");
        if (guard?.Invoke(at) == false) return AgentActionResult.Failure("采集保护检查未通过。");
        var kind = tree ? ToolType.Axe : ToolType.Pickaxe;
        var tool = inventoryOnly ? ToolSelector.FindInventoryTool(context.Commander, kind) : ToolSelector.FindBestTool(context.Commander, kind);
        if (tool == null) return AgentActionResult.Failure(tree ? "背包缺少斧头。" : "背包缺少镐。");
        if (!tree && !ToolSelector.CanMineTile(tool, cell.TileType)) return AgentActionResult.Failure("镐力不足，未挖掘。");
        var npc = context.Agent;
        var target = new Vector2(at.X * 16 + 8, at.Y * 16 + 8);
        if (Vector2.Distance(npc.Center, target) > Reach)
        {
            _walk ??= new MoveAction(at, Reach / 16);
            var travel = _walk.Tick(context);
            if (travel.Status == AgentActionStatus.Failure) return AgentActionResult.Failure("无法到达采集目标：" + travel.Message);
            return AgentActionResult.Pending("正在靠近采集目标。");
        }
        Cancel(); MovementHelper.Hold(npc);
        if (!MovementHelper.IsOnGround(npc) && !MovementHelper.InWater(npc)) return AgentActionResult.Pending("等待站稳后采集。");
        int power = tree ? tool.axe : tool.pick;
        float rate = tree ? power / 20f : power / (float)Math.Max(1, ToolSelector.GetTileStrength(cell.TileType));
        _work += Math.Clamp(rate, .5f, 2f);
        if (_work < 100) return AgentActionResult.Pending($"{(tree ? "砍树" : "挖矿")} · {(int)_work}%");
        if (guard?.Invoke(at) == false || !WorldGen.CanKillTile(at.X, at.Y)) return AgentActionResult.Failure("世界或模组保护阻止本次采集。");
        int before = cell.TileType;
        WorldGen.KillTile(at.X, at.Y, noItem: false);
        cell = Framing.GetTileSafely(at.X, at.Y);
        record(at.X, at.Y, before, cell.HasTile ? cell.TileType : null);
        if (Main.netMode == NetmodeID.Server) NetMessage.SendTileSquare(-1, at.X, at.Y, 1);
        return !cell.HasTile ? AgentActionResult.Success(tree ? "树干移除已核实。" : "方块移除已核实。")
            : AgentActionResult.Failure("方块没有移除，已停止；不会把尝试当作完成。");
    }
}
