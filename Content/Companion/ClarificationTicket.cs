using System;
using System.Collections.Generic;

namespace TerrarAI.Content.Companion;

public sealed record ClarificationTicket(Guid Session, int Player, string EpisodeId, string Source,
    string Question, string? ResumeCommand, bool FeedbackOnly, long Revision, bool Day, DateTimeOffset Expires,
    IReadOnlyList<PreferenceUpdate>? ScopeCandidates = null, int Attempts = 1)
{
    public bool Matches(Guid session, int player, long revision, bool day, DateTimeOffset now) =>
        Session == session && Player == player && Revision == revision && Day == day && now < Expires;
}
