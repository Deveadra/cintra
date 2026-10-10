using MeetingCompanion.Contracts;

namespace MeetingCompanion.Core;

public sealed record ConversationRetention
{
    public long WindowMs { get; init; } = 600_000;
    public int MaxEvents { get; init; } = 2048;
    public int MaxTextCharacters { get; init; } = 262_144;
    public int MaxIssues { get; init; } = 128;
    public int MaxOverlaps { get; init; } = 256;
    public long HealthFreshnessMs { get; init; } = 2000;
    public int MaxStatementCharacters { get; init; } = 2048;
    public int MaxEvidencePerStatement { get; init; } = 16;

    internal void Validate()
    {
        if (WindowMs <= 0 || MaxEvents <= 0 || MaxTextCharacters <= 0 || MaxIssues <= 0 ||
            MaxStatementCharacters <= 0 || MaxEvidencePerStatement <= 0 || MaxOverlaps <= 0 || HealthFreshnessMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(ConversationRetention));
    }
}

public enum IssueField
{
    CurrentTopic, OpenQuestion, ReportedError, ObservedEnvironment, Hypothesis, ConfirmedFact,
    AttemptedStep, FailedStep, SuggestedStep, UnresolvedQuestion
}

public sealed record RecordedIssue(Guid Id, IssueField Field, long RecordedAtMs, EvidenceStatement Statement);
public sealed record TurnOverlap(Guid FirstTurnId, Guid SecondTurnId, long StartMs, long EndMs);
public sealed record ConversationView(ConversationEvent[] Events, TranscriptPartial[] Partials,
    TurnOverlap[] Overlaps, ComponentHealth[] Health, SessionState State, RecordedIssue[] Issues,
    Guid[] InterruptedTurnIds, bool OverlapsTruncated, int RetainedTextCharacters, bool Stopped);

public sealed record ConversationSnapshot(ConversationContext Context, ConversationView View);
