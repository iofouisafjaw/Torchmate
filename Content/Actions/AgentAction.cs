// TerrarAI replacement implementation, 2026-10-20. See docs/source-replacement.md.
using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using TerrarAI.Content.Companion;
using TerrarAI.Content.Systems;
namespace TerrarAI.Content.Actions;

// Replacement lifecycle: terminal results are sticky, cleanup runs on every exit.
public abstract class AgentAction
{
    private AgentActionResult? _terminal;
    private AgentActionContext? _context;
    private int _age;
    private readonly List<TileMutation> _effects = new();
    public abstract string Name { get; }
    public bool Cancelled { get; private set; }
    public IReadOnlyList<TileMutation> Mutations => _effects;
    protected virtual int MaxExecutionTicks => 600;
    public virtual bool CanFailAndGiveUp => true;
    public virtual float GetRequiredRange() => 0;
    public virtual Point? GetTargetTile() => null;
    public virtual Vector2? GetTargetPosition() => null;
    protected void RecordMutation(int x, int y, int? before, int? after)
    { if (before != after) _effects.Add(new(x, y, before, after)); }
    internal AgentActionResult Tick(AgentActionContext context)
    {
        if (_terminal is { } finished) return finished;
        if (Cancelled) return AgentActionResult.Failure("动作已取消。");
        if (!ServerAuthority.IsServer) return End(context, AgentActionResult.Failure("客户端不能执行世界动作。"));
        _context = context;
        try
        {
            if (++_age > MaxExecutionTicks) return End(context, AgentActionResult.Failure("动作超时，已停止执行。"));
            var step = OnTick(context);
            return step.Status == AgentActionStatus.Pending ? step : End(context, step);
        }
        catch (Exception error) { return End(context, AgentActionResult.Failure("动作执行失败：" + error.Message)); }
    }
    private AgentActionResult End(AgentActionContext context, AgentActionResult result)
    { _terminal = result; OnExit(context, result); return result; }
    public void Cancel()
    {
        if (Cancelled) return;
        Cancelled = true; OnCancel();
        if (_context is { } context) MovementHelper.Stop(context.Agent);
        _terminal ??= AgentActionResult.Failure("动作已取消。");
    }
    public virtual void Reset()
    {
        Cancel(); _terminal = null; _context = null; _age = 0; Cancelled = false; _effects.Clear();
    }
    protected virtual void OnCancel() { }
    protected virtual void OnExit(AgentActionContext context, AgentActionResult result) => MovementHelper.Stop(context.Agent);
    protected abstract AgentActionResult OnTick(AgentActionContext context);
}
public readonly record struct AgentActionContext(NPC Agent, Player? Commander)
{
    public static AgentActionContext From(NPC agent, Player? commander = null) => new(agent, commander);
}
public enum AgentActionStatus { Pending, Success, Failure }
public readonly record struct AgentActionResult(AgentActionStatus Status, string? Message = null, object? Payload = null)
{
    public static AgentActionResult Pending(string? message = null, object? payload = null) => new(AgentActionStatus.Pending, message, payload);
    public static AgentActionResult Success(string? message = null, object? payload = null) => new(AgentActionStatus.Success, message, payload);
    public static AgentActionResult Failure(string? message = null, object? payload = null) => new(AgentActionStatus.Failure, message, payload);
}
