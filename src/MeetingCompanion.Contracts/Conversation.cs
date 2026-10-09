using System.Text.Json.Serialization;

namespace MeetingCompanion.Contracts;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "event_type")]
[JsonDerivedType(typeof(TranscriptPartial), "transcript_partial")]
[JsonDerivedType(typeof(TranscriptFinal), "transcript_final")]
[JsonDerivedType(typeof(AudioGapEvent), "audio_gap")]
[JsonDerivedType(typeof(SourceChangedEvent), "source_changed")]
[JsonDerivedType(typeof(SnapshotAdded), "snapshot_added")]
[JsonDerivedType(typeof(TriggerDetected), "trigger_detected")]
[JsonDerivedType(typeof(SuggestionGenerated), "suggestion_generated")]
[JsonDerivedType(typeof(SessionStateEvent), "session_state")]
public abstract record ConversationEvent
{
    public required int SchemaVersion { get; init; }
    public required Guid EventId { get; init; }
    public required Guid SessionId { get; init; }
    public required DateTimeOffset ReceivedUtc { get; init; }

    public virtual void Validate()
    {
        Require.That(SchemaVersion == ContractVersion.Current, "Unsupported event schema version.");
        Require.Id(EventId);
        Require.Id(SessionId);
        Require.That(ReceivedUtc.Offset == TimeSpan.Zero, "Receipt timestamp must be UTC.");
    }
}

public abstract record TranscriptEvent : ConversationEvent
{
    public required AudioStreamId StreamId { get; init; }
    public required Guid TurnId { get; init; }
    public required string? ProviderItemId { get; init; }
    public required long CapturedStartMs { get; init; }
    public required long CapturedEndMs { get; init; }
    public required string Text { get; init; }
    public required Attribution Attribution { get; init; }
    public required AttributionConfidence AttributionConfidence { get; init; }
    public required string? ConfidenceNote { get; init; }

    public override void Validate()
    {
        base.Validate();
        Require.Enum(StreamId);
        Require.Enum(Attribution);
        Require.Enum(AttributionConfidence);
        Require.Id(TurnId);
        Require.Range(CapturedStartMs, CapturedEndMs);
        Require.Text(Text);
        Require.That(ProviderItemId is null || ProviderItemId.Length <= 256, "Provider item ID too long.");
        Require.That(ConfidenceNote is null || ConfidenceNote.Length <= 1024, "Confidence note too long.");
        Require.That(StreamId != AudioStreamId.EndpointFallback || Attribution is Attribution.Mixed or Attribution.Unknown,
            "Unisolated endpoint audio must be labeled MIXED or UNKNOWN.");
        Require.That(StreamId != AudioStreamId.RemoteApp || Attribution != Attribution.You, "Remote app audio cannot be attributed to YOU.");
    }
}

public sealed record TranscriptPartial : TranscriptEvent;
public sealed record TranscriptFinal : TranscriptEvent;
public sealed record AudioGapEvent : ConversationEvent
{
    public required CaptureGap Gap { get; init; }
    public override void Validate() { base.Validate(); Require.That(Gap is not null, "Gap required."); Gap!.Validate(); }
}
public sealed record SourceChangedEvent : ConversationEvent
{
    public required CaptureSourceChanged Change { get; init; }
    public override void Validate() { base.Validate(); Require.That(Change is not null, "Change required."); Change!.Validate(); }
}
public sealed record SnapshotAdded : ConversationEvent
{
    public required SnapshotMetadata Snapshot { get; init; }
    public override void Validate() { base.Validate(); Require.That(Snapshot is not null, "Snapshot required."); Snapshot!.Validate(); }
}
public sealed record TriggerDetected : ConversationEvent
{
    public required Guid TriggerId { get; init; }
    public required TriggerKind Kind { get; init; }
    public required Guid[] EvidenceEventIds { get; init; }
    public override void Validate()
    {
        base.Validate(); Require.Id(TriggerId); Require.Enum(Kind);
        Require.That(EvidenceEventIds is { Length: <= 128 }, "Invalid evidence list.");
        foreach (var id in EvidenceEventIds!) Require.Id(id);
    }
}
public sealed record SuggestionGenerated : ConversationEvent
{
    public required Suggestion Suggestion { get; init; }
    public override void Validate() { base.Validate(); Require.That(Suggestion is not null, "Suggestion required."); Suggestion!.Validate(); }
}
public sealed record SessionStateEvent : ConversationEvent
{
    public required SessionState State { get; init; }
    public required ComponentHealth[] Health { get; init; }
    public override void Validate()
    {
        base.Validate(); Require.Enum(State);
        Require.That(Health is { Length: <= 6 }, "Invalid health list.");
        foreach (var entry in Health!) { Require.That(entry is not null, "Health entry required."); entry!.Validate(); }
        Require.That(Health.Select(x => x.Component).Distinct().Count() == Health.Length, "Duplicate component health.");
    }
}

public sealed record PixelRegion(int X, int Y, int Width, int Height);
public sealed record SnapshotMetadata(Guid SnapshotId, long CapturedAtMs, SnapshotSource Source,
    string SourceLabel, PixelRegion? Crop, int Width, int Height)
{
    public void Validate()
    {
        Require.Id(SnapshotId); Require.Enum(Source); Require.Text(SourceLabel, 256);
        Require.That(CapturedAtMs >= 0 && Width is > 0 and <= 16384 && Height is > 0 and <= 16384, "Invalid snapshot dimensions or time.");
        Require.That(Crop is null || (Crop.Width > 0 && Crop.Height > 0), "Invalid crop.");
        Require.That(Source != SnapshotSource.Region || Crop is not null, "Region capture requires a crop.");
    }
}

/// <summary>Evidence IDs refer to conversation events, not provider item IDs.</summary>
public sealed record EvidenceReference(EvidenceKind Kind, Guid? EventId, Guid? SnapshotId, string? Assumption);
public sealed record EvidenceStatement(string Text, EvidenceReference[] Evidence);
public sealed record ConversationContext(Guid SessionId, long AsOfMs, string Summary,
    TranscriptFinal[] SpeakerTurns, EvidenceStatement? CurrentTopic, EvidenceStatement? OpenQuestion,
    EvidenceStatement? ReportedError, EvidenceStatement[] ObservedEnvironment,
    EvidenceStatement[] Hypotheses, EvidenceStatement[] ConfirmedFacts, EvidenceStatement[] AttemptedSteps,
    EvidenceStatement[] FailedSteps, EvidenceStatement[] SuggestedSteps, EvidenceStatement[] UnresolvedQuestions,
    Guid? LastSnapshotId);
