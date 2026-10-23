// TerrarAI replacement implementation, 2026-10-20. See docs/source-replacement.md.
namespace TerrarAI.Content.Actions;
public sealed class FailureAction(string message) : AgentAction
{
    public override string Name => "failure";
    protected override AgentActionResult OnTick(AgentActionContext context) =>
        AgentActionResult.Failure(string.IsNullOrWhiteSpace(message) ? "当前动作无法执行。" : message);
}
