using System;
using System.Collections.Generic;
using System.Linq;

namespace TerrarAI.Content.Companion;

public sealed record TileMutation(int X, int Y, int? BeforeType, int? AfterType)
{
    public bool Removed => BeforeType.HasValue && !AfterType.HasValue;
}

public sealed record TaskReference(string EpisodeId, string TaskId, string Command);

public sealed class EpisodeRecorder
{
    private readonly CompanionStore _store;
    private readonly Dictionary<string, (string Episode, string? Turn)> _tasks = new();
    private readonly HashSet<string> _closed = new();
    public EpisodeRecorder(CompanionStore store) => _store = store;

    public string Begin(string? turnId, string message, object snapshot, string? relatedEpisodeId = null)
    {
        string id = Guid.NewGuid().ToString("N");
        _store.Record("episode_opened", null, null, new { message, snapshot, relatedEpisodeId }, id, turnId);
        return id;
    }

    public void Link(string episodeId, string? turnId, string taskId, string command)
    {
        _tasks[taskId] = (episodeId, turnId);
        // Keep only the most recent in-session links. The full history remains on disk.
        if (_tasks.Count > 128) _tasks.Remove(_tasks.Keys.First());
        _store.SetLastTask(new TaskReference(episodeId, taskId, command));
    }

    public void Record(string kind, string? taskId, string? decisionId, object data,
        string? episodeId = null, string? turnId = null)
    {
        if (taskId != null && _tasks.TryGetValue(taskId, out var link))
        { episodeId ??= link.Episode; turnId ??= link.Turn; }
        _store.Record(kind, taskId, decisionId, data, episodeId, turnId);
    }

    public void Close(string episodeId, string reason)
    {
        if (!_closed.Add(episodeId)) return;
        _store.Record("episode_closed", null, null, new { reason, goalVerified = false }, episodeId);
        if (_closed.Count > 256) _closed.Remove(_closed.First());
    }
}

public sealed record DecisionMeasurement(string ProposedMode, string Policy, bool ModelAsked,
    bool ForcedAsk, bool UnnecessaryAsk, bool DeniedProposal, bool MissingMode)
{
    public static DecisionMeasurement Measure(CompanionDecision decision, PolicyResult expected) => new(
        decision.Mode, expected.ToString(), decision.Mode == "ask",
        expected == PolicyResult.Ask && decision.Mode != "ask",
        expected == PolicyResult.Allow && decision.Mode == "ask",
        expected == PolicyResult.Deny, decision.Mode == "unspecified");
}
