using System.Text.Json;
using MeetingCompanion.Contracts;
using MeetingCompanion.Core;

namespace MeetingCompanion.Ai;

internal sealed record VisionContext(ConversationContext Context, Guid[] EvidenceIds);

internal static class VisionContextBuilder
{
    // No unbounded summary or newest context: all evidence comes from one capture-time snapshot.
    public static VisionContext Build(ConversationSnapshot snapshot, SnapshotMetadata image, Guid imageEventId, int maxCharacters, DateTimeOffset captureUtc)
    {
        var source = snapshot.Context;
        var available = snapshot.View.Events.Where(e => e.ReceivedUtc <= captureUtc).ToArray();
        var turns = source.SpeakerTurns.Where(t => t.ReceivedUtc <= captureUtc).TakeLast(8).Select(t => t with { Text = Clip(t.Text, 512), ConfidenceNote = t.ConfidenceNote is null ? null : Clip(t.ConfidenceNote, 128) }).ToArray();
        var gaps = available.OfType<AudioGapEvent>().TakeLast(8).ToArray();
        var changes = available.OfType<SourceChangedEvent>().TakeLast(8).ToArray();
        var interrupted = turns.Where(t => gaps.Any(g => g.Gap.StreamId == t.StreamId && g.Gap.CapturedStartMs <= t.CapturedEndMs && g.Gap.CapturedEndMs >= t.CapturedStartMs) ||
            changes.Any(c => c.Change.Source.StreamId == t.StreamId && c.Change.EffectiveAtMs >= t.CapturedStartMs && c.Change.EffectiveAtMs <= t.CapturedEndMs) ||
            available.OfType<SessionStateEvent>().Any(s => s.Health.Any(h => h.ObservedAtMs >= t.CapturedStartMs && h.ObservedAtMs <= t.CapturedEndMs &&
                h.Status is HealthStatus.NoDevice or HealthStatus.Disconnected or HealthStatus.Failed or HealthStatus.PermissionDenied or HealthStatus.WrongProcess or HealthStatus.Reconnecting &&
                (t.StreamId == AudioStreamId.LocalMic ? h.Component is Component.LocalCapture or Component.LocalStt : h.Component is Component.RemoteCapture or Component.RemoteStt))))
            .Select(t => t.TurnId).ToArray();
        var ids = turns.Select(t => t.EventId).Concat(gaps.Select(g => g.EventId)).Concat(changes.Select(c => c.EventId)).Append(imageEventId).ToHashSet();
        bool omitted = turns.Length != source.SpeakerTurns.Length || snapshot.View.Events.OfType<AudioGapEvent>().Count() > gaps.Length ||
            snapshot.View.Events.OfType<SourceChangedEvent>().Count() > changes.Length;
        EvidenceStatement[] Statements(EvidenceStatement[] values) => values.TakeLast(4)
            .Where(s => s.Evidence.All(e => e.Kind == EvidenceKind.Transcript && e.EventId is Guid id && ids.Contains(id)))
            .Select(s => new EvidenceStatement(Clip(s.Text, 512), s.Evidence.ToArray())).ToArray();
        EvidenceStatement? Statement(EvidenceStatement? s) => s is null ? null : Statements([s]).SingleOrDefault();
        var context = source with
        {
            SpeakerTurns = turns,
            CurrentTopic = Statement(source.CurrentTopic),
            OpenQuestion = Statement(source.OpenQuestion),
            ReportedError = Statement(source.ReportedError),
            ObservedEnvironment = Statements(source.ObservedEnvironment),
            Hypotheses = Statements(source.Hypotheses),
            ConfirmedFacts = Statements(source.ConfirmedFacts),
            AttemptedSteps = Statements(source.AttemptedSteps),
            FailedSteps = Statements(source.FailedSteps),
            SuggestedSteps = Statements(source.SuggestedSteps),
            UnresolvedQuestions = Statements(source.UnresolvedQuestions),
            LastSnapshotId = image.SnapshotId,
            Summary = JsonSerializer.Serialize(new
            {
                context_may_be_missing_or_evicted = true, // MC-007 does not expose an eviction ledger: never claim complete history.
                no_retained_final_turns = turns.Length == 0,
                context_omitted = omitted,
                text_truncated = source.SpeakerTurns.Any(t => t.Text.Length > 512),
                issue_view_may_be_omitted_or_truncated = true,
                received_after_capture_omitted = available.Length != snapshot.View.Events.Length,
                interrupted_turn_ids = interrupted,
                partial_turns_omitted = available.OfType<TranscriptPartial>().Any(),
                snapshot.View.OverlapsTruncated,
                gaps = gaps.Select(g => new { g.EventId, g.SessionId, g.Gap.StreamId, g.Gap.CapturedStartMs, g.Gap.CapturedEndMs, g.Gap.Reason }),
                source_changes = changes.Select(c => new { c.EventId, c.Change.EffectiveAtMs, c.Change.Source.StreamId }),
                snapshot_event_id = imageEventId,
                snapshot_id = image.SnapshotId,
                attribution_warning = "REMOTE is mixed application audio, not an identified speaker. MIXED/UNKNOWN/inferred attribution is uncertain. Interrupted turns are incomplete."
            }, ContractJson.Options)
        };
        // Reject instead of silently clipping JSON or sending unresolved issue evidence.
        if (JsonSerializer.Serialize(context, ContractJson.Options).Length > maxCharacters)
            throw new ContractException("Bounded vision context exceeded.");
        return new(context, ids.ToArray());
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
