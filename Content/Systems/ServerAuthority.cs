// TerrarAI replacement implementation, 2026-10-20. See docs/source-replacement.md.
using System;
using Terraria;
using Terraria.ID;
namespace TerrarAI.Content.Systems;
public static class ServerAuthority
{
    public static bool IsServer => Main.netMode is NetmodeID.SinglePlayer or NetmodeID.Server;
    public static void QueueMainThread(Action? callback)
    { if (callback is { } work) Main.QueueMainThreadAction(work); }
    public static bool EnsureServer(Action<string> reply, string? denyMessage = null)
    {
        bool allowed = IsServer;
        if (!allowed) reply(denyMessage ?? "当前客户端不能直接执行世界操作。");
        return allowed;
    }
}
