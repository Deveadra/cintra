using System.Text.Json;
using MeetingCompanion.Contracts;
using MeetingCompanion.Core;

namespace MeetingCompanion.Unit.Tests;

public sealed class RollingConversationStoreTests
{
    private static readonly Guid Session = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly ClockMapping Mapping = new(1000, 10_000, DateTimeOffset.UnixEpoch);
    private static RollingConversationStore Store(ConversationRetention? limits = null) => new(Session, Mapping, limits);
    private static Guid Id(int n) => new(n, 0, 0, new byte[8]);
    private static TranscriptFinal Final(int n, long start, long end, AudioStreamId stream = AudioStreamId.LocalMic) => new()
    {
        SchemaVersion = 1,
        EventId = Id(n),
        SessionId = Session,
        ReceivedUtc = DateTimeOffset.UnixEpoch.AddMilliseconds(n),
        StreamId = stream,
        TurnId = Id(n + 10000),
        ProviderItemId = null,
        CapturedStartMs = start,
        CapturedEndMs = end,
        Text = $"observed-{n}",
        Attribution = stream == AudioStreamId.LocalMic ? Attribution.You : Attribution.Remote,
        AttributionConfidence = AttributionConfidence.SourceConfirmed,
        ConfidenceNote = null
    };
    private static TranscriptPartial Partial(TranscriptFinal final, int id, string text) => new()
    {
        SchemaVersion = 1,
        EventId = Id(id),
        SessionId = Session,
        ReceivedUtc = DateTimeOffset.UnixEpoch.AddMilliseconds(id),
        StreamId = final.StreamId,
        TurnId = final.TurnId,
        ProviderItemId = final.ProviderItemId,
        CapturedStartMs = final.CapturedStartMs,
        CapturedEndMs = final.CapturedEndMs,
        Text = text,
        Attribution = final.Attribution,
        AttributionConfidence = final.AttributionConfidence,
        ConfidenceNote = null
    };
    private static SessionStateEvent Health(int n, Component component, HealthStatus status, long time, SessionState state = SessionState.Degraded) => new()
    {
        SchemaVersion = 1,
        EventId = Id(n),
        SessionId = Session,
        ReceivedUtc = DateTimeOffset.UnixEpoch,
        State = state,
        Health = [new(component, status, time, null)]
    };
    private static RecordedIssue Issue(int n, IssueField field, TranscriptFinal final) => new(Id(n), field,
        final.CapturedEndMs, new("explicit observation", [new(EvidenceKind.Transcript, final.EventId, null, null)]));

    [Fact]
    public void ReplayPermutationsProduceIdenticalCaptureOrderAndOverlap()
    {
        var fixture = new ConversationEvent[] { Final(1, 0, 100), Final(2, 50, 120, AudioStreamId.RemoteApp), Final(3, 130, 150) };
        string? expected = null;
        for (var seed = 0; seed < 40; seed++)
        {
            var store = Store();
            var random = new Random(seed);
            foreach (var item in fixture.OrderBy(_ => random.Next())) store.Apply(item);
            var result = JsonSerializer.Serialize(new { Context = store.GetContext(150), View = store.GetView(150) });
            expected ??= result;
            Assert.Equal(expected, result);
            Assert.Single(store.GetView(150).Overlaps);
            Assert.Equal(new long[] { 0, 50, 130 }, store.GetContext(150).SpeakerTurns.Select(x => x.CapturedStartMs));
        }
    }

    [Fact]
    public void PartialRevisionsAndFinalDuplicatesNeverRewriteAcceptedFinal()
    {
        var store = Store();
        var final = Final(1, 0, 100);
        store.Apply(Partial(final, 20, "new partial"));
        store.Apply(Partial(final, 10, "old partial"));
        Assert.Equal("new partial", Assert.Single(store.GetView(100).Partials).Text);
        store.Apply(final);
        store.Apply(final);
        store.Apply(final with { EventId = Id(99), Text = "conflicting duplicate" });
        store.Apply(Partial(final, 100, "late partial"));
        Assert.Empty(store.GetView(100).Partials);
        Assert.Equal(final, Assert.Single(store.GetContext(100).SpeakerTurns));
    }

    [Fact]
    public void CaptureGapsSourcesAndOutOfOrderHealthRemainVisible()
    {
        var store = Store();
        store.Apply(new AudioGapEvent
        {
            SchemaVersion = 1,
            EventId = Id(1),
            SessionId = Session,
            ReceivedUtc = DateTimeOffset.UnixEpoch,
            Gap = new(AudioStreamId.RemoteApp, 100, 130, GapReason.DeviceLost, 720)
        });
        store.Apply(new SourceChangedEvent
        {
            SchemaVersion = 1,
            EventId = Id(2),
            SessionId = Session,
            ReceivedUtc = DateTimeOffset.UnixEpoch,
            Change = new(new(AudioStreamId.RemoteApp, "replacement", null, 123, true, false), 140)
        });
        store.Apply(Health(3, Component.RemoteCapture, HealthStatus.Disconnected, 150));
        store.Apply(Health(4, Component.RemoteCapture, HealthStatus.Healthy, 90, SessionState.Listening));
        var view = store.GetView(150);
        Assert.Single(view.Events.OfType<AudioGapEvent>());
        Assert.Single(view.Events.OfType<SourceChangedEvent>());
        Assert.Equal(HealthStatus.Disconnected, Assert.Single(view.Health).Status);
        Assert.Equal(SessionState.Degraded, view.State);
        Assert.Empty(store.GetContext(150).ConfirmedFacts);
    }

    [Fact]
    public void FactsRequireFinalEvidenceAndReturnedArraysCannotMutateStore()
    {
        var store = Store();
        var final = Final(1, 0, 100);
        store.Apply(Partial(final, 2, "maybe"));
        Assert.Throws<ContractException>(() => store.RecordIssue(Issue(3, IssueField.ConfirmedFact, final)));
        store.Apply(final);
        foreach (var field in Enum.GetValues<IssueField>()) store.RecordIssue(Issue(100 + (int)field, field, final));
        var context = store.GetContext(100);
        Assert.Single(context.ConfirmedFacts);
        Assert.Single(context.AttemptedSteps);
        Assert.Single(context.UnresolvedQuestions);
        context.ConfirmedFacts[0].Evidence[0] = new(EvidenceKind.Assumption, null, null, "invented");
        Assert.Equal(final.EventId, store.GetContext(100).ConfirmedFacts[0].Evidence[0].EventId);
        Assert.Empty(store.GetContext(99).SpeakerTurns);
        Assert.Empty(store.GetContext(99).ConfirmedFacts);
        Assert.True(store.ResolveIssue(Id(100 + (int)IssueField.OpenQuestion)));
        Assert.Null(store.GetContext(100).OpenQuestion);
    }

    [Fact]
    public void SnapshotCorrelationUsesSharedCaptureClockAndMetadataOnly()
    {
        var store = Store();
        var clock = store.Clock;
        Assert.Equal(100, clock.FromMonotonicTicks(2000));
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMilliseconds(100), clock.ToUtc(100));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.FromMonotonicTicks(999));
        var snapshot = new SnapshotMetadata(Id(9), 100, SnapshotSource.Region, "selected crop", new(0, 0, 10, 10), 10, 10);
        store.Apply(new SnapshotAdded
        {
            SchemaVersion = 1,
            EventId = Id(8),
            SessionId = Session,
            ReceivedUtc = DateTimeOffset.UnixEpoch.AddDays(1),
            Snapshot = snapshot
        });
        store.RecordIssue(new(Id(7), IssueField.ReportedError, 100,
            new("visible error supplied by caller", [new(EvidenceKind.Snapshot, Id(8), Id(9), null)])));
        Assert.Null(store.GetContext(99).LastSnapshotId);
        Assert.Equal(Id(9), store.GetContext(100).LastSnapshotId);
        Assert.NotNull(store.GetContext(100).ReportedError);
    }

    [Fact]
    public void TimeCountTextAndIssueBoundsPruneEvidenceWithoutDanglingFacts()
    {
        var store = Store(new() { WindowMs = 100, MaxEvents = 4, MaxTextCharacters = 60, MaxIssues = 2 });
        var first = Final(1, 0, 10);
        store.Apply(first);
        for (var n = 0; n < 5; n++) store.RecordIssue(Issue(100 + n, IssueField.ConfirmedFact, first));
        Assert.Equal(2, store.GetContext(10).ConfirmedFacts.Length);
        for (var n = 2; n < 2000; n++) store.Apply(Final(n, n * 10, n * 10 + 5));
        var view = store.GetView(20000);
        Assert.InRange(view.Events.Length, 0, 4);
        Assert.InRange(view.RetainedTextCharacters, 0, 60);
        Assert.Empty(view.Issues);
        store.Apply(first); // Very late duplicate cannot reintroduce expired evidence.
        Assert.DoesNotContain(store.GetContext(20000).SpeakerTurns, x => x.EventId == first.EventId);
        store.Apply(Final(2001, 20001, 20002) with { Text = new string('x', 100) });
        Assert.InRange(store.GetView(20002).RetainedTextCharacters, 0, 60);
    }

    [Fact]
    public async Task ConcurrentApplyReadAndStopCannotResurrectContent()
    {
        var store = Store(new() { MaxEvents = 64 });
        await Task.WhenAll(Enumerable.Range(1, 500).Select(n => Task.Run(() =>
        {
            var final = Final(n, n, n + 1);
            store.Apply(final);
            store.Apply(final);
            Assert.InRange(store.GetView(1000).Events.Length, 0, 64);
        })));
        Assert.Equal(64, store.GetContext(1000).SpeakerTurns.Length);
        await Task.WhenAll(Task.Run(store.Stop), Task.Run(() =>
        {
            for (var n = 501; n < 800; n++) store.Apply(Final(n, n, n + 1));
        }));
        store.Apply(Final(999, 999, 1000));
        Assert.Empty(store.GetContext(1000).SpeakerTurns);
        Assert.Empty(store.GetView(1000).Events);
        Assert.True(store.GetView(1000).Stopped);
        Assert.Throws<InvalidOperationException>(() => store.RecordIssue(Issue(1000, IssueField.ConfirmedFact, Final(999, 999, 1000))));
    }

    [Theory]
    [InlineData(SessionState.Stopped)]
    [InlineData(SessionState.Stopping)]
    public void StopStateAndClearAreTerminal(SessionState state)
    {
        var store = Store();
        store.Apply(Final(1, 0, 100));
        store.Apply(Health(2, Component.LocalCapture, HealthStatus.Stopped, 100, state));
        store.Clear();
        store.Apply(Final(3, 100, 200));
        Assert.Empty(store.GetView(200).Events);
        Assert.Equal(SessionState.Stopped, store.GetView(200).State);
        Assert.Equal("", store.GetContext(200).Summary);
    }

    [Fact]
    public void InvalidOrForeignInputsFailBeforeMutation()
    {
        var store = Store();
        Assert.Throws<ContractException>(() => store.Apply(Final(1, 0, 100) with { SessionId = Id(8) }));
        Assert.Throws<ContractException>(() => store.Apply(Final(1, 100, 99)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Store(new() { MaxEvents = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.GetContext(-1));
        Assert.Empty(store.GetView(100).Events);
    }
    [Fact]
    public void InterruptionsOverlapCapAndStaleHealthAreHonest()
    {
        var store = Store(new() { MaxOverlaps = 1, HealthFreshnessMs = 100 });
        store.Apply(Final(1, 0, 100));
        store.Apply(Final(2, 10, 100, AudioStreamId.RemoteApp));
        store.Apply(Final(3, 20, 100, AudioStreamId.RemoteApp));
        store.Apply(new AudioGapEvent
        {
            SchemaVersion = 1,
            EventId = Id(4),
            SessionId = Session,
            ReceivedUtc = DateTimeOffset.UnixEpoch,
            Gap = new(AudioStreamId.RemoteApp, 50, 60, GapReason.NetworkLost, 240)
        });
        foreach (var component in new[] { Component.LocalCapture, Component.RemoteCapture, Component.LocalStt, Component.RemoteStt })
            store.Apply(Health(10 + (int)component, component, HealthStatus.Healthy, 100, SessionState.Listening));
        var view = store.GetView(100);
        Assert.Single(view.Overlaps);
        Assert.True(view.OverlapsTruncated);
        Assert.Equal(new[] { Id(10002), Id(10003) }, view.InterruptedTurnIds);
        Assert.Equal(SessionState.Listening, view.State);
        Assert.Equal(SessionState.Degraded, store.GetView(201).State);
        Assert.All(store.GetView(201).Health, h => Assert.Equal(HealthStatus.Unknown, h.Status));
        Assert.Empty(store.GetContext(700000).SpeakerTurns);
    }

    [Fact]
    public void HealthArraysAndIssueInputsAreOwnedCopies()
    {
        var store = Store();
        var report = Health(1, Component.RemoteCapture, HealthStatus.Disconnected, 100);
        store.Apply(report);
        report.Health[0] = new(Component.RemoteCapture, HealthStatus.Healthy, 100, null);
        var view = store.GetView(100);
        Assert.Equal(HealthStatus.Disconnected, Assert.Single(view.Health).Status);
        Assert.Single(view.Events.OfType<SessionStateEvent>()).Health[0] = report.Health[0];
        Assert.Equal(HealthStatus.Disconnected, Assert.Single(store.GetView(100).Health).Status);
        var final = Final(2, 0, 100);
        store.Apply(final);
        var issue = Issue(3, IssueField.ConfirmedFact, final);
        store.RecordIssue(issue);
        issue.Statement.Evidence[0] = new(EvidenceKind.Transcript, Id(99), null, null);
        Assert.Single(store.GetContext(100).ConfirmedFacts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamCancellationAndFailureAlwaysClear(bool fail)
    {
        var store = Store();
        using var cancellation = new CancellationTokenSource();
        async IAsyncEnumerable<ConversationEvent> Source()
        {
            yield return Final(1, 0, 100);
            await Task.Yield();
            if (fail) throw new IOException("fixture failure");
            cancellation.Cancel();
            yield return Final(2, 100, 200);
        }
        if (fail) await Assert.ThrowsAsync<IOException>(() => store.ConsumeAsync(Source(), cancellation.Token));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ConsumeAsync(Source(), cancellation.Token));
        Assert.Empty(store.GetView(200).Events);
        Assert.True(store.GetView(200).Stopped);
    }

    [Fact]
    public async Task CompletionClearsAndLargeIssueOrUnobservedFactIsRejected()
    {
        var store = Store(new() { MaxStatementCharacters = 30, MaxEvidencePerStatement = 1 });
        var final = Final(1, 0, 100);
        store.Apply(final);
        var issue = Issue(2, IssueField.ReportedError, final);
        Assert.Throws<ContractException>(() => store.RecordIssue(issue with { Statement = issue.Statement with { Text = new string('x', 31) } }));
        Assert.Throws<ContractException>(() => store.RecordIssue(issue with { Statement = new("guess", [new(EvidenceKind.Assumption, null, null, "guess")]) }));
        Assert.Throws<ContractException>(() => store.RecordIssue(issue with { Statement = new("future tool", [new(EvidenceKind.ToolResult, Id(4), null, null)]) }));
        async IAsyncEnumerable<ConversationEvent> Source()
        {
            await Task.Yield();
            yield return final;
        }
        await store.ConsumeAsync(Source(), CancellationToken.None);
        Assert.Empty(store.GetContext(100).SpeakerTurns);
        Assert.True(store.GetView(100).Stopped);
    }
    [Fact]
    public void SourceReplacementRequiresFreshCaptureAndSttAndAtomicSnapshotMatches()
    {
        var store = Store();
        foreach (var component in new[] { Component.LocalCapture, Component.RemoteCapture, Component.LocalStt, Component.RemoteStt })
            store.Apply(Health(10 + (int)component, component, HealthStatus.Healthy, 100, SessionState.Listening));
        store.Apply(new SourceChangedEvent
        {
            SchemaVersion = 1,
            EventId = Id(2),
            SessionId = Session,
            ReceivedUtc = DateTimeOffset.UnixEpoch,
            Change = new(new(AudioStreamId.RemoteApp, "replacement", null, 123, true, false), 150)
        });
        store.Apply(Health(20, Component.RemoteCapture, HealthStatus.Healthy, 160, SessionState.Listening));
        Assert.Equal(SessionState.Degraded, store.GetView(160).State);
        store.Apply(Health(21, Component.RemoteStt, HealthStatus.Healthy, 170, SessionState.Listening));
        Assert.Equal(SessionState.Listening, store.GetView(170).State);
        var final = Final(1, 150, 180);
        store.Apply(final);
        var snapshot = store.GetSnapshot(180);
        Assert.Equal(snapshot.Context.SpeakerTurns, snapshot.View.Events.OfType<TranscriptFinal>());
    }
}
