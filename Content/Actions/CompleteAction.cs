// TerrarAI replacement implementation, 2026-10-20. See docs/source-replacement.md.
using Microsoft.Xna.Framework;
using TerrarAI.Content.Systems;
namespace TerrarAI.Content.Actions;
public sealed class CompleteAction(string message) : AgentAction
{
    public override string Name => "Complete";
    protected override AgentActionResult OnTick(AgentActionContext context)
    {
        string text = string.IsNullOrWhiteSpace(message) ? "这一步结束了。" : message.Trim();
        CompanionMessages.Send(text, Color.LightBlue);
        return AgentActionResult.Success(text);
    }
}
