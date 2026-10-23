// TerrarAI replacement implementation, 2026-10-20. See docs/source-replacement.md.
using TerrarAI.Content.Systems;
using Terraria.ModLoader;
namespace TerrarAI.Common.Commands;
// Compatibility command; all permission checks remain in the shared entry point.
public sealed class AgentCommand : ModCommand
{
    public override string Command => "action";
    public override string Description => "Send a task to the local companion.";
    public override string Usage => "/action <task>";
    public override CommandType Type => CommandType.Chat;
    public override void Action(CommandCaller caller, string input, string[] args) => ChatCoordinator.HandleInput(caller, string.Join(' ', args));
}
