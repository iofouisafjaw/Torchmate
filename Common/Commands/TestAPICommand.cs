// TerrarAI replacement implementation, 2026-10-20. See docs/source-replacement.md.
using System;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria.ModLoader;
using TerrarAI.Content.Companion;
using TerrarAI.Content.Systems;
namespace TerrarAI.Common.Commands;
public class TestAPICommand : ModCommand
{
    public override string Command => "testqwen";
    public override string Description => "Validate the local model response without running it.";
    public override string Usage => "/testqwen";
    public override CommandType Type => CommandType.Chat;
    internal static bool IsChecking { get; private set; }
    public override void Action(CommandCaller caller, string input, string[] args) => Check((text, color) =>
    { CompanionUISystem.Publish(text, color); caller.Reply(text, color); });
    internal static void Check(Action<string, Color> reply)
    {
        if (!ServerAuthority.EnsureServer(text => reply(text, Color.OrangeRed))) return;
        if (IsChecking) { reply("连接检查正在进行。", Color.LightBlue); return; }
        IsChecking = true;
        var world = CompanionMemorySystem.Session;
        reply("正在检查本地模型连接……", Color.LightBlue);
        // Invoke the client on the game thread so config/world reads stay on it.
        Task<string> request;
        try { request = TerrarAI.RequireClient().SendChatCompletionAsync(CompanionProtocol.SystemPrompt,
            "Return one short hello with the say action.", cancellationToken: CompanionMemorySystem.WorldToken); }
        catch (Exception error) { IsChecking = false; reply(error.Message, Color.OrangeRed); return; }
        _ = Deliver();
        async Task Deliver()
        {
            string message; Color tint;
            try { CompanionProtocol.Parse(await request.ConfigureAwait(false)); message = "本地模型连接通过，动作格式有效。"; tint = Color.LightGreen; }
            catch (Exception error) { message = "连接检查失败：" + error.Message; tint = Color.OrangeRed; }
            ServerAuthority.QueueMainThread(() => { IsChecking = false; if (world == CompanionMemorySystem.Session) reply(message, tint); });
        }
    }
}
public sealed class TestXaiAlias : TestAPICommand { public override string Command => "testxai"; }
