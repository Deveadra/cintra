using MeetingCompanion.Contracts;

namespace MeetingCompanion.Core;

/// <summary>Single-session, memory-only store. Apply/Get/Clear are linearized under one lock.</summary>
public sealed class RollingConversationStore : IConversationStore
{
    private readonly object gate = new();
    private readonly Guid sessionId;
    private readonly ConversationRetention retention;
    private readonly Dictionary<Guid, ConversationEvent> events = new();
    private readonly Dictionary<Guid, RecordedIssue> issues = new();
    private long watermark;
    private bool stopped;
    public SessionClock Clock { get; }

    public RollingConversationStore(Guid sessionId, ClockMapping clock, ConversationRetention? retention = null)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Session ID required.", nameof(sessionId));
        this.sessionId = sessionId;
        Clock = new SessionClock(clock);
        this.retention = retention ?? new();
        this.retention.Validate();
    }

    public void Apply(ConversationEvent conversationEvent)
    {
        ArgumentNullException.ThrowIfNull(conversationEvent);
        conversationEvent.Validate();
        if (conversationEvent.SessionId != sessionId) throw new ContractException("Event belongs to another session.");
        // Trigger/suggestion state is owned by MC-010/011; do not infer issue facts from it.
        if (conversationEvent is not (TranscriptEvent or AudioGapEvent or SourceChangedEvent or SnapshotAdded or SessionStateEvent))
            throw new NotSupportedException("This event is not conversation evidence.");
        lock (gate)
        {
            if (stopped) return;
            if (conversationEvent is SessionStateEvent { State: SessionState.Stopped or SessionState.Stopping })
            {
                ClearCore();
                return;
            }
            if (events.ContainsKey(conversationEvent.EventId)) return;
            if (conversationEvent is TranscriptEvent incoming)
            {
                var existing = events.Values.OfType<TranscriptEvent>().FirstOrDefault(x =>
                    x.StreamId == incoming.StreamId && x.TurnId == incoming.TurnId);
                if (existing is TranscriptFinal) return; // Accepted finals are immutable, including new-ID duplicates.
                if (existing is not null)
                {
                    if (incoming is TranscriptPartial && CompareRevision(incoming, existing) <= 0) return;
                    events.Remove(existing.EventId);
                }
            }
            var time = Time(conversationEvent);
            watermark = Math.Max(watermark, time);
            if (time >= Cutoff)
                events.Add(conversationEvent.EventId, Clone(conversationEvent));
            Prune();
        }
    }

    /// <summary>Explicit observations only. Caller supplies classification and resolvable evidence; no NLP inference.</summary>
    public void RecordIssue(RecordedIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        lock (gate)
        {
            if (stopped) throw new InvalidOperationException("Session has stopped.");
            if (issue.Id == Guid.Empty || !Enum.IsDefined(issue.Field) || issue.RecordedAtMs < Cutoff ||
                issue.RecordedAtMs > watermark || issue.Statement is null ||
                string.IsNullOrWhiteSpace(issue.Statement.Text) || issue.Statement.Text.Length > retention.MaxStatementCharacters ||
                issue.Statement.Evidence is not { Length: > 0 } ||
                issue.Statement.Evidence.Length > retention.MaxEvidencePerStatement ||
                !issue.Statement.Evidence.All(e => Resolves(e, issue.RecordedAtMs)))
                throw new ContractException("Issue requires bounded, retained evidence available at its capture time.");
            if (issues.ContainsKey(issue.Id)) return;
            issues.Add(issue.Id, CopyIssue(issue));
            Prune();
        }
    }

    public bool ResolveIssue(Guid id)
    {
        lock (gate) return issues.Remove(id);
    }

    public ConversationContext GetContext(long asOfMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(asOfMs);
        lock (gate)
        {
            var visible = Visible(asOfMs);
            var active = ActiveIssues(asOfMs);
            EvidenceStatement[] Statements(IssueField field) => active.Where(x => x.Field == field)
                .Select(x => CopyStatement(x.Statement)).ToArray();
            EvidenceStatement? Latest(IssueField field) => Statements(field).LastOrDefault();
            var summary = string.Join("\n", active.Select(x => $"{x.Field}: {x.Statement.Text}"));
            return new(sessionId, asOfMs, summary, visible.OfType<TranscriptFinal>().ToArray(),
                Latest(IssueField.CurrentTopic), Latest(IssueField.OpenQuestion), Latest(IssueField.ReportedError),
                Statements(IssueField.ObservedEnvironment), Statements(IssueField.Hypothesis),
                Statements(IssueField.ConfirmedFact), Statements(IssueField.AttemptedStep), Statements(IssueField.FailedStep),
                Statements(IssueField.SuggestedStep), Statements(IssueField.UnresolvedQuestion),
                visible.OfType<SnapshotAdded>().LastOrDefault()?.Snapshot.SnapshotId);
        }
    }

    /// <summary>Atomic context plus gap/health view for MC-009/010; arrays are detached from the store.</summary>
    public ConversationSnapshot GetSnapshot(long asOfMs)
    {
        lock (gate) return new(GetContext(asOfMs), GetView(asOfMs));
    }

    public ConversationView GetView(long asOfMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(asOfMs);
        lock (gate)
        {
            var visible = Visible(asOfMs);
            var turns = visible.OfType<TranscriptEvent>().ToArray();
            var overlaps = new List<TurnOverlap>();
            var overlapsTruncated = false;
            for (var i = 0; i < turns.Length; i++)
                for (var j = i + 1; j < turns.Length; j++)
                {
                    if (turns[j].CapturedStartMs >= turns[i].CapturedEndMs) break;
                    if (turns[i].StreamId == turns[j].StreamId) continue;
                    var start = Math.Max(turns[i].CapturedStartMs, turns[j].CapturedStartMs);
                    var end = Math.Min(turns[i].CapturedEndMs, turns[j].CapturedEndMs);
                    if (start < end)
                    {
                        if (overlaps.Count < retention.MaxOverlaps) overlaps.Add(new(turns[i].TurnId, turns[j].TurnId, start, end));
                        else overlapsTruncated = true;
                    }
                }
            var health = visible.OfType<SessionStateEvent>().SelectMany(x => x.Health)
                .Where(x => x.ObservedAtMs <= asOfMs).GroupBy(x => x.Component)
                .Select(x => x.OrderBy(h => h.ObservedAtMs).ThenBy(h => h.Status).ThenBy(h => h.DiagnosticCode, StringComparer.Ordinal).Last())
                .Select(x => asOfMs - x.ObservedAtMs > retention.HealthFreshnessMs ? x with { Status = HealthStatus.Unknown, DiagnosticCode = "stale_health" } : x)
                .OrderBy(x => x.Component).ToArray();
            var state = visible.OfType<SessionStateEvent>().LastOrDefault()?.State ?? SessionState.Starting;
            if (stopped) state = SessionState.Stopped;
            else if (health.Any(x => x.Status is not (HealthStatus.Healthy or HealthStatus.Silence))) state = SessionState.Degraded;
            else if (visible.OfType<SourceChangedEvent>().Any(s =>
                !(s.Change.Source.StreamId == AudioStreamId.LocalMic ? new[] { Component.LocalCapture, Component.LocalStt } :
                    new[] { Component.RemoteCapture, Component.RemoteStt }).All(c => health.Any(h => h.Component == c &&
                        h.ObservedAtMs > s.Change.EffectiveAtMs && h.Status is HealthStatus.Healthy or HealthStatus.Silence)))) state = SessionState.Degraded;
            else if (state == SessionState.Listening && !new[] { Component.LocalCapture, Component.RemoteCapture, Component.LocalStt, Component.RemoteStt }
                .All(c => health.Any(h => h.Component == c))) state = SessionState.Starting;
            var interrupted = turns.Where(t => visible.Any(e => Interrupts(e, t))).Select(t => t.TurnId).Distinct().ToArray();
            return new(visible.Select(Clone).ToArray(), visible.OfType<TranscriptPartial>().ToArray(), overlaps.ToArray(),
                health, state, ActiveIssues(asOfMs).Select(CopyIssue).ToArray(), interrupted, overlapsTruncated, TextSize, stopped);
        }
    }

    /// <summary>Consume MC-006 ReadAllAsync with backpressure and clear on completion, cancellation or failure.</summary>
    public async Task ConsumeAsync(IAsyncEnumerable<ConversationEvent> source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Apply(item);
            }
        }
        finally { Stop(); }
    }

    public void Clear() { lock (gate) ClearCore(); }
    public void Stop() => Clear();
    private void ClearCore() { events.Clear(); issues.Clear(); watermark = 0; stopped = true; }
    private long Cutoff => Math.Max(0, watermark - retention.WindowMs);
    private int TextSize => events.Values.Sum(TextLength);

    private void Prune()
    {
        foreach (var id in events.Where(x => Time(x.Value) < Cutoff).Select(x => x.Key).ToArray()) events.Remove(id);
        while (events.Count > retention.MaxEvents || TextSize > retention.MaxTextCharacters)
            events.Remove(Ordered().First().EventId);
        foreach (var id in issues.Where(x => x.Value.RecordedAtMs < Cutoff ||
            !x.Value.Statement.Evidence.All(e => Resolves(e, watermark))).Select(x => x.Key).ToArray()) issues.Remove(id);
        while (issues.Count > retention.MaxIssues)
            issues.Remove(issues.Values.OrderBy(x => x.RecordedAtMs).ThenBy(x => x.Id).First().Id);
    }

    private RecordedIssue[] ActiveIssues(long asOfMs) => issues.Values.Where(x => x.RecordedAtMs <= asOfMs && x.RecordedAtMs >= Math.Max(Cutoff, asOfMs - retention.WindowMs) &&
        x.Statement.Evidence.All(e => Resolves(e, asOfMs))).OrderBy(x => x.RecordedAtMs).ThenBy(x => x.Id).ToArray();

    private bool Resolves(EvidenceReference? evidence, long asOfMs)
    {
        if (evidence is null || evidence.Assumption is not null) return false;
        if (evidence.Kind == EvidenceKind.Transcript && evidence.SnapshotId is null && evidence.EventId is Guid id)
            return events.TryGetValue(id, out var entry) && entry is TranscriptFinal && Time(entry) <= asOfMs && Time(entry) >= Math.Max(Cutoff, asOfMs - retention.WindowMs);
        if (evidence.Kind == EvidenceKind.Snapshot && evidence.SnapshotId is Guid snapshotId)
            return events.Values.OfType<SnapshotAdded>().Any(x => x.Snapshot.SnapshotId == snapshotId && Time(x) <= asOfMs && Time(x) >= Math.Max(Cutoff, asOfMs - retention.WindowMs) &&
                (evidence.EventId is null || evidence.EventId == x.EventId));
        return false;
    }

    private ConversationEvent[] Visible(long asOfMs) => Ordered().Where(x => Time(x) <= asOfMs &&
        Time(x) >= Math.Max(Cutoff, asOfMs - retention.WindowMs)).ToArray();

    private static bool Interrupts(ConversationEvent e, TranscriptEvent turn)
    {
        bool Point(long time) => time >= turn.CapturedStartMs && time <= turn.CapturedEndMs;
        return e switch
        {
            AudioGapEvent g when g.Gap.StreamId == turn.StreamId =>
                g.Gap.CapturedStartMs <= turn.CapturedEndMs && g.Gap.CapturedEndMs >= turn.CapturedStartMs,
            SourceChangedEvent s when s.Change.Source.StreamId == turn.StreamId => Point(s.Change.EffectiveAtMs),
            SessionStateEvent s => s.Health.Any(h => Point(h.ObservedAtMs) &&
                h.Status is HealthStatus.NoDevice or HealthStatus.Disconnected or HealthStatus.Failed or
                    HealthStatus.PermissionDenied or HealthStatus.WrongProcess or HealthStatus.Reconnecting &&
                (turn.StreamId == AudioStreamId.LocalMic ? h.Component is Component.LocalCapture or Component.LocalStt :
                    h.Component is Component.RemoteCapture or Component.RemoteStt)),
            _ => false
        };
    }

    private IOrderedEnumerable<ConversationEvent> Ordered() => events.Values.OrderBy(StartTime).ThenBy(Time)
        .ThenBy(x => x is TranscriptEvent t ? (int)t.StreamId : -1)
        .ThenBy(x => x is TranscriptEvent t ? t.TurnId : Guid.Empty).ThenBy(x => x.EventId);
    private long Time(ConversationEvent e) => e switch
    {
        TranscriptEvent t => t.CapturedEndMs,
        AudioGapEvent g => g.Gap.CapturedEndMs,
        SourceChangedEvent s => s.Change.EffectiveAtMs,
        SnapshotAdded s => s.Snapshot.CapturedAtMs,
        SessionStateEvent s => s.Health.Length > 0 ? s.Health.Max(x => x.ObservedAtMs) : Clock.FromUtc(s.ReceivedUtc),
        _ => throw new NotSupportedException()
    };
    private long StartTime(ConversationEvent e) => e switch
    {
        TranscriptEvent t => t.CapturedStartMs,
        AudioGapEvent g => g.Gap.CapturedStartMs,
        _ => Time(e)
    };
    private static int CompareRevision(TranscriptEvent a, TranscriptEvent b)
    {
        var time = a.ReceivedUtc.CompareTo(b.ReceivedUtc);
        return time != 0 ? time : a.EventId.CompareTo(b.EventId);
    }
    private static int TextLength(ConversationEvent e) => e switch
    {
        TranscriptEvent t => t.Text.Length + (t.ProviderItemId?.Length ?? 0) + (t.ConfidenceNote?.Length ?? 0),
        SourceChangedEvent s => s.Change.Source.DisplayName.Length + (s.Change.Source.DeviceId?.Length ?? 0),
        SnapshotAdded s => s.Snapshot.SourceLabel.Length,
        SessionStateEvent s => s.Health.Sum(x => x.DiagnosticCode?.Length ?? 0),
        _ => 0
    };
    private static ConversationEvent Clone(ConversationEvent e) => e is SessionStateEvent s ? s with { Health = s.Health.ToArray() } : e;
    private static EvidenceStatement CopyStatement(EvidenceStatement s) => s with { Evidence = s.Evidence.ToArray() };
    private static RecordedIssue CopyIssue(RecordedIssue i) => i with { Statement = CopyStatement(i.Statement) };
}
