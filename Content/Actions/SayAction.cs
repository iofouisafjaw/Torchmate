// TerrarAI replacement implementation, 2026-10-20. See docs/source-replacement.md.
using Microsoft.Xna.Framework;
using TerrarAI.Content.Systems;
namespace TerrarAI.Content.Actions;
public sealed class SayAction(string text) : AgentAction
{
    public override string Name => "say";
    protected override AgentActionResult OnTick(AgentActionContext context)
    {
        string message = string.IsNullOrWhiteSpace(text) ? "……" : text.Trim();
        CompanionMessages.Send(message, Color.LightGoldenrodYellow);
        return AgentActionResult.Success(message);
    }
}
