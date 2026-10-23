// TerrarAI replacement implementation, 2026-10-20. See docs/source-replacement.md.
using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using TerrarAI.Content.Actions;
using TerrarAI.Content.Companion;
namespace TerrarAI.Content.Systems;

// Only the current validated wire protocol can create executable actions.
public static class ActionParser
{
    public static IReadOnlyList<AgentAction> Parse(string json, NPC agent, Player? commander = null, bool singleTileMining = false)
    {
        try
        {
            if (!ServerAuthority.IsServer) throw new InvalidOperationException("客户端不能提交世界动作。");
            var proposal = CompanionProtocol.Parse(json);
            Point at = default;
            if (proposal.Type is "mine" or "chop" or "move")
            {
                at = new(proposal.Parameters.GetProperty("tileX").GetInt32(), proposal.Parameters.GetProperty("tileY").GetInt32());
                if (!WorldGen.InWorld(at.X, at.Y, 2)) throw new FormatException("目标不在可操作的世界范围内。");
            }
            if (proposal.Type is "mine" or "chop")
            {
                var kind = proposal.Type == "mine" ? ToolType.Pickaxe : ToolType.Axe;
                if (ToolSelector.FindBestTool(commander, kind) == null) throw new FormatException("背包中没有所需采集工具。");
            }
            // Never silently move the exact target that a player approved.
            AgentAction action = proposal.Type switch
            {
                "move" => new MoveAction(at), "mine" => new MineAction(at, singleTile: singleTileMining), "chop" => new ChopAction(at),
                "say" => new SayAction(proposal.Parameters.GetProperty("text").GetString() ?? ""),
                "complete" => new CompleteAction(proposal.Parameters.GetProperty("message").GetString() ?? ""),
                _ => throw new FormatException("不支持的动作类型。")
            };
            return new[] { action };
        }
        catch (Exception error) when (error is FormatException or JsonException or InvalidOperationException or ArgumentException)
        { throw new ActionParserException(error.Message); }
    }
}
public sealed class ActionParserException(string message) : Exception(message);
