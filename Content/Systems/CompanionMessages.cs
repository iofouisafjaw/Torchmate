// TerrarAI replacement implementation, 2026-10-20. See docs/source-replacement.md.
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.Chat;
using Terraria.Localization;
namespace TerrarAI.Content.Systems;
internal static class CompanionMessages
{
    internal static void Send(string text, Color color)
    {
        if (Main.netMode == NetmodeID.SinglePlayer) CompanionUISystem.Message(text, color);
        else if (Main.netMode == NetmodeID.Server) ChatHelper.BroadcastChatMessage(NetworkText.FromLiteral(text), color);
    }
}
