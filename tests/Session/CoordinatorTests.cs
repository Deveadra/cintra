using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MeetingCompanion.Capture;
using MeetingCompanion.Contracts;
using MeetingCompanion.Platform;
using MeetingCompanion.Session;

namespace MeetingCompanion.Session.Tests;

public sealed class CoordinatorTests
{
    [Fact]
    public async Task SelectStartTwoStreamsPartialFinalSnapshotPauseResumeStopRestart()
    {
        var fixture = new Fixture();
        await using var coordinator = fixture.Create();
        var choice = await fixture.Choose(coordinator);
        await coordinator.StartAsync(choice, ProviderMode.OfflineTest, default);
        var first = fixture.Factory.Latest;
        var id = coordinator.View.SessionId!.Value;
        Assert.Equal(SessionState.Listening, coordinator.View.State);
        Assert.Equal(0.25, coordinator.View.LocalPeak);
        Assert.Equal(0.5, coordinator.View.RemotePeak);
        Assert.Equal(choice.Microphone.Id, first.Selection!.Microphone.DeviceId);
        Assert.Equal(choice.Candidate.ProcessId, first.Selection.Incoming.ProcessId);
        var turn = Guid.NewGuid();
        first.Emit(Transcript(id, turn, AudioStreamId.LocalMic, false, "provisional"));
        await Until(() => coordinator.View.Transcripts.Any(t => t.Text == "provisional"));
        first.Emit(Transcript(id, turn, AudioStreamId.LocalMic, true, "YOU final"));
        first.Emit(Transcript(id, Guid.NewGuid(), AudioStreamId.RemoteApp, true, "REMOTE final"));
        first.Emit(Transcript(id, turn, AudioStreamId.LocalMic, false, "stale delta"));
        await Until(() => coordinator.View.Transcripts.OfType<TranscriptFinal>().Count() == 2);
        Assert.DoesNotContain(coordinator.View.Transcripts, t => t.Text == "stale delta");
        var ticket = coordinator.BeginSnapshot()!;
        Assert.Same(first.Clock, ticket.Clock);
        var backend = new Pixels(ticket.Clock.MonotonicOriginTicks + fixture.Time.Milliseconds);
        var result = await new SnapshotCapture(backend, ticket.Clock).CaptureAsync(new(SnapshotSource.SelectedWindow, 1, "synthetic error"), default);
        Assert.True(coordinator.AcceptSnapshot(ticket, result.Metadata, result.Png));
        var handoff = coordinator.LatestSnapshot!;
        Assert.Equal(fixture.Time.Milliseconds, handoff.Metadata.CapturedAtMs);
        Assert.Equal(id, handoff.Context.SessionId);
        Assert.Equal(2, handoff.Context.SpeakerTurns.Length);
        Assert.Equal(result.Metadata.SnapshotId, handoff.Context.LastSnapshotId);
        await coordinator.PauseAsync(default);
        Assert.Equal(SessionState.Paused, coordinator.View.State);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(0, fixture.Inventory.Leases[0].Disposals);
        Assert.All(handoff.Png, b => Assert.Equal(0, b));
        Assert.False(coordinator.AcceptSnapshot(ticket, result.Metadata, result.Png));
        first.Observe(Healthy(AudioStreamId.LocalMic, 1000)); // Late native callbacks cannot restore Listening.
        Assert.Equal(SessionState.Paused, coordinator.View.State);
        await coordinator.ResumeAsync(default);
        Assert.Equal(id, coordinator.View.SessionId);
        Assert.Same(first.Clock, fixture.Factory.Latest.Clock);
        Assert.NotSame(first, fixture.Factory.Latest);
        Assert.Equal(2, coordinator.View.Gaps.Length);
        await coordinator.StopAsync();
        Assert.Empty(coordinator.View.Transcripts);
        Assert.Null(coordinator.LatestSnapshot);
        Assert.Equal(1, fixture.Inventory.Leases[0].Disposals);
        await coordinator.StartAsync(choice, ProviderMode.OfflineTest, default);
        Assert.NotEqual(id, coordinator.View.SessionId);
        first.Observe(Healthy(AudioStreamId.LocalMic, 1000));
        fixture.Factory.Latest.Emit(Transcript(id, Guid.NewGuid(), AudioStreamId.RemoteApp, true, "wrong meeting"));
        await coordinator.PollAsync(default);
        Assert.Empty(coordinator.View.Transcripts);
    }

    [Fact]
    public async Task CaptureOnlyIsDegradedNeverListeningAndCannotSendProviderCalls()
    {
        var fixture = new Fixture();
        await using var coordinator = fixture.Create();
        await coordinator.StartAsync(await fixture.Choose(coordinator), ProviderMode.Unavailable, default);
        Assert.Equal(SessionState.Degraded, coordinator.View.State);
        Assert.Contains("unavailable", coordinator.View.Detail);
        Assert.All(coordinator.View.Health.Where(h => h.Component is Component.LocalStt or Component.RemoteStt), h => Assert.Equal(HealthStatus.Failed, h.Status));
        await coordinator.PollAsync(default);
        Assert.Equal(SessionState.Degraded, coordinator.View.State);
    }

    [Theory]
    [InlineData("recycled")]
    [InlineData("renderer")]
    [InlineData("lease")]
    public async Task StaleRootRendererOrLeaseStopsAndRequiresFreshChoice(string change)
    {
        var fixture = new Fixture();
        await using var coordinator = fixture.Create();
        var choice = await fixture.Choose(coordinator);
        await coordinator.StartAsync(choice, ProviderMode.OfflineTest, default);
        if (change == "recycled") fixture.Inventory.Snapshot = fixture.Inventory.Snapshot with
        { Processes = [fixture.Inventory.Snapshot.Processes[0] with { Identity = fixture.Identity with { CreationFileTime = 200 } }] };
        if (change == "renderer") fixture.Inventory.Snapshot = fixture.Inventory.Snapshot with
        { Sessions = [fixture.Inventory.Snapshot.Sessions[0] with { SessionId = "changed" }] };
        if (change == "lease") fixture.Inventory.Leases[0].Alive = false;
        await coordinator.PollAsync(default);
        await Until(() => fixture.Inventory.Leases[0].Disposals == 1);
        Assert.Equal(SessionState.Error, coordinator.View.State);
        Assert.Empty(coordinator.View.Transcripts);
        Assert.Equal(1, fixture.Factory.Latest.Disposals);
        await Assert.ThrowsAsync<CallSourceSelectionException>(() => coordinator.StartAsync(choice, ProviderMode.OfflineTest, default));
    }

    [Theory]
    [InlineData(HealthStatus.Disconnected)]
    [InlineData(HealthStatus.NoDevice)]
    [InlineData(HealthStatus.WrongProcess)]
    [InlineData(HealthStatus.PermissionDenied)]
    public async Task HelperAndCaptureFailuresStopBothStreams(HealthStatus failure)
    {
        var fixture = new Fixture();
        await using var coordinator = fixture.Create();
        await coordinator.StartAsync(await fixture.Choose(coordinator), ProviderMode.OfflineTest, default);
        fixture.Factory.Latest.Observe(new CaptureHealth(AudioStreamId.RemoteApp,
            new(Component.RemoteCapture, failure, 1000, "capture_fault"), 0, 0, 0));
        await Until(() => fixture.Inventory.Leases[0].Disposals == 1);
        Assert.Equal(SessionState.Error, coordinator.View.State);
        Assert.Contains("capture_fault", coordinator.View.Detail);
        Assert.Empty(coordinator.View.Health);
    }

    [Fact]
    public async Task SourceChangedClearsVoiceAndStopsInsteadOfFollowingPid()
    {
        var fixture = new Fixture();
        await using var coordinator = fixture.Create();
        var choice = await fixture.Choose(coordinator);
        await coordinator.StartAsync(choice, ProviderMode.OfflineTest, default);
        fixture.Factory.Latest.Observe(new CaptureSourceChanged(new(AudioStreamId.RemoteApp, "replacement", null, 999, true, false), 1000));
        await Until(() => fixture.Inventory.Leases[0].Disposals == 1);
        Assert.Single(fixture.Factory.Created);
        Assert.Contains("source_changed", coordinator.View.Detail);
    }

    [Fact]
    public async Task OneSideProviderFailureCannotLeaveFalseListeningAndCanRecover()
    {
        var fixture = new Fixture();
        await using var coordinator = fixture.Create();
        await coordinator.StartAsync(await fixture.Choose(coordinator), ProviderMode.OfflineTest, default);
        var runtime = fixture.Factory.Latest;
        runtime.Transport = [new(Component.LocalStt, HealthStatus.Healthy, 1000, null), new(Component.RemoteStt, HealthStatus.Reconnecting, 1000, "socket_lost")];
        await coordinator.PollAsync(default);
        Assert.Equal(SessionState.Degraded, coordinator.View.State);
        var revision = coordinator.View.Revision;
        runtime.Emit(new SessionStateEvent
        {
            SchemaVersion = 1,
            EventId = Guid.NewGuid(),
            SessionId = coordinator.View.SessionId!.Value,
            ReceivedUtc = DateTimeOffset.UnixEpoch,
            State = SessionState.Listening,
            Health = [new(Component.LocalStt, HealthStatus.Healthy, 1000, null), new(Component.RemoteStt, HealthStatus.Healthy, 1000, null)]
        });
        await Until(() => coordinator.View.Revision > revision);
        Assert.Equal(SessionState.Degraded, coordinator.View.State); // Repeated aggregate entries are not new observations.
        runtime.Transport = [new(Component.LocalStt, HealthStatus.Healthy, 1000, null), new(Component.RemoteStt, HealthStatus.Healthy, 1000, null)];
        await coordinator.PollAsync(default);
        Assert.Equal(SessionState.Listening, coordinator.View.State);
        fixture.Time.Milliseconds += 3000;
        // The view must expire health even while a source-inventory probe is blocked.
        Assert.Equal(SessionState.Degraded, coordinator.View.State);
        Assert.Contains(coordinator.View.Health, h => h.DiagnosticCode == "stale_health");
    }

    [Fact]
    public async Task CallerCancellationAfterStartupStopsAndLateSnapshotCannotEnterNewMeeting()
    {
        var fixture = new Fixture();
        await using var coordinator = fixture.Create();
        var choice = await fixture.Choose(coordinator);
        using var cancellation = new CancellationTokenSource();
        await coordinator.StartAsync(choice, ProviderMode.OfflineTest, cancellation.Token);
        var ticket = coordinator.BeginSnapshot()!;
        cancellation.Cancel();
        await Until(() => fixture.Inventory.Leases[0].Disposals == 1);
        Assert.Equal(SessionState.Error, coordinator.View.State);
        await coordinator.StartAsync(await fixture.Choose(coordinator), ProviderMode.OfflineTest, default);
        Assert.False(coordinator.AcceptSnapshot(ticket, new(Guid.NewGuid(), 1000, SnapshotSource.SelectedWindow, "old", null, 1, 1), [1]));
        Assert.Null(coordinator.LatestSnapshot);
    }

    [Fact]
    public async Task StopDuringStartupCancelsPromptlyAndDisposesLeaseExactlyOnce()
    {
        var fixture = new Fixture();
        fixture.Factory.ReadyOnStart = false;
        await using var coordinator = fixture.Create();
        var starting = coordinator.StartAsync(await fixture.Choose(coordinator), ProviderMode.OfflineTest, default);
        await Until(() => fixture.Factory.Created.Count == 1);
        var stopping = coordinator.StopAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Factory.Latest.Disposals);
        Assert.Equal(1, fixture.Inventory.Leases[0].Disposals);
        Assert.Equal(SessionState.Stopped, coordinator.View.State);
    }

    [Fact]
    public async Task StartupDeadlineAndCallerCancellationReleaseResources()
    {
        var fixture = new Fixture();
        fixture.Factory.ReadyOnStart = false;
        await using var coordinator = fixture.Create(new() { StartupDeadline = TimeSpan.FromMilliseconds(50) });
        await fixture.Choose(coordinator);
        await Assert.ThrowsAsync<TimeoutException>(() => coordinator.StartAsync(awaitChoice(), ProviderMode.OfflineTest, default));
        Assert.Equal(1, fixture.Inventory.Leases[0].Disposals);
        var cancelledFixture = new Fixture();
        cancelledFixture.Factory.ReadyOnStart = false;
        await using var cancelledCoordinator = cancelledFixture.Create();
        using var cancellation = new CancellationTokenSource();
        var start = cancelledCoordinator.StartAsync(await cancelledFixture.Choose(cancelledCoordinator), ProviderMode.OfflineTest, cancellation.Token);
        await Until(() => cancelledFixture.Factory.Created.Count == 1);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(1, cancelledFixture.Inventory.Leases[0].Disposals);
        MeetingChoice awaitChoice() => fixture.Choice!;
    }

    [Fact]
    public async Task CleanupDeadlineKeepsLeaseAndBlocksRestartAndClosureUntilCleanupActuallyCompletes()
    {
        var fixture = new Fixture();
        await using var coordinator = fixture.Create(new() { CleanupDeadline = TimeSpan.FromMilliseconds(50) });
        var choice = await fixture.Choose(coordinator);
        await coordinator.StartAsync(choice, ProviderMode.OfflineTest, default);
        fixture.Factory.Latest.DisposeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await Assert.ThrowsAsync<TimeoutException>(() => coordinator.StopAsync(new CancellationToken(true)));
        Assert.Equal(SessionState.Error, coordinator.View.State);
        Assert.Equal(0, fixture.Inventory.Leases[0].Disposals);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync(choice, ProviderMode.OfflineTest, default));
        await Assert.ThrowsAsync<TimeoutException>(() => coordinator.DisposeAsync().AsTask());
        fixture.Factory.Latest.DisposeGate.SetResult();
        await coordinator.StopAsync();
        Assert.Equal(1, fixture.Inventory.Leases[0].Disposals);
        Assert.Equal(1, fixture.Factory.Latest.Disposals);
        Assert.Equal(SessionState.Stopped, coordinator.View.State);
    }

    [Fact]
    public async Task FailedGracefulStopIsReportedEvenWhenFallbackDisposalReleasedResources()
    {
        var fixture = new Fixture();
        await using var coordinator = fixture.Create();
        await coordinator.StartAsync(await fixture.Choose(coordinator), ProviderMode.OfflineTest, default);
        fixture.Factory.Latest.ShutdownFailure = true;
        await Assert.ThrowsAsync<IOException>(() => coordinator.StopAsync());
        Assert.Equal(SessionState.Error, coordinator.View.State);
        Assert.Equal(1, fixture.Factory.Latest.Disposals);
        Assert.Equal(1, fixture.Inventory.Leases.Single().Disposals);
        Assert.Null(coordinator.View.SessionId);
        await coordinator.StopAsync();
        Assert.Equal(SessionState.Stopped, coordinator.View.State);
    }

    [Fact]
    public async Task PollsNeverOverlapAndOldPollCannotInvalidateRestartedMeeting()
    {
        var fixture = new Fixture();
        await using var coordinator = fixture.Create();
        var choice = await fixture.Choose(coordinator);
        await coordinator.StartAsync(choice, ProviderMode.OfflineTest, default);
        fixture.Inventory.ReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var polling = coordinator.PollAsync(default);
        await Until(() => fixture.Inventory.Waiting);
        await coordinator.PollAsync(default);
        Assert.Equal(1, fixture.Inventory.ConcurrentReads);
        await coordinator.StopAsync();
        fixture.Inventory.ReadGate.SetResult();
        await polling;
        fixture.Inventory.ReadGate = null;
        await coordinator.StartAsync(choice, ProviderMode.OfflineTest, default);
        Assert.Equal(SessionState.Listening, coordinator.View.State);
    }

    [Fact]
    public async Task LateFailedProbeIsFencedFromNewMeetingAndCancelledPollIsHarmless()
    {
        var fixture = new Fixture();
        await using var coordinator = fixture.Create();
        var choice = await fixture.Choose(coordinator);
        await coordinator.StartAsync(choice, ProviderMode.OfflineTest, default);
        fixture.Inventory.ReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Inventory.FailBlockedRead = true;
        var oldProbe = coordinator.PollAsync(default);
        await Until(() => fixture.Inventory.Waiting);
        await coordinator.StopAsync();
        fixture.Inventory.ReadGate = null;
        await coordinator.StartAsync(choice, ProviderMode.OfflineTest, default);
        fixture.Inventory.ReleaseBlockedRead!.SetResult();
        await oldProbe;
        await coordinator.PollAsync(new CancellationToken(true));
        Assert.Equal(SessionState.Listening, coordinator.View.State);
        Assert.Equal(0, fixture.Inventory.Leases[1].Disposals);
    }

    [Theory]
    [InlineData("mic")]
    [InlineData("ambiguous")]
    [InlineData("unsupported")]
    [InlineData("identity")]
    public async Task InvalidFreshSelectionsFailBeforeStartingCapture(string condition)
    {
        var fixture = new Fixture();
        await using var coordinator = fixture.Create();
        var choice = await fixture.Choose(coordinator);
        fixture.Inventory.Snapshot = condition switch
        {
            "mic" => fixture.Inventory.Snapshot with { Endpoints = fixture.Inventory.Snapshot.Endpoints.Where(e => !e.IsCapture).ToArray() },
            "ambiguous" => fixture.Inventory.Snapshot with { Sessions = [fixture.Inventory.Snapshot.Sessions[0] with { MultiProcess = true }] },
            "unsupported" => fixture.Inventory.Snapshot with { ProcessLoopbackSupported = false },
            _ => fixture.Inventory.Snapshot with { Processes = [fixture.Inventory.Snapshot.Processes[0] with { Identity = null }] }
        };
        await Assert.ThrowsAsync<CallSourceSelectionException>(() => coordinator.StartAsync(choice, ProviderMode.OfflineTest, default));
        Assert.Empty(fixture.Factory.Created);
        Assert.Empty(fixture.Inventory.Leases);
    }

    [Fact]
    public async Task NoKeyOrUnapprovedLiveModeNeverReadsKeyOrStartsCapture()
    {
        int reads = 0;
        int captures = 0;
        var factory = new MeetingRuntimeFactory("unused", () => false, () => { reads++; return "test"; }, () => { captures++; throw new InvalidOperationException(); });
        Assert.Throws<InvalidOperationException>(() => factory.Create(Guid.NewGuid(), ProviderMode.OpenAi, _ => { }));
        Assert.Equal(0, reads);
        Assert.Equal(0, captures);
        factory = new("unused", () => true, () => null, () => { captures++; throw new InvalidOperationException(); });
        Assert.Throws<InvalidOperationException>(() => factory.Create(Guid.NewGuid(), ProviderMode.OpenAi, _ => { }));
        Assert.Equal(0, captures);
        await Task.CompletedTask;
    }

    internal static async Task Until(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Deterministic observable condition was not reached.");
            await Task.Delay(1);
        }
    }
    internal static CaptureHealth Healthy(AudioStreamId stream, long now) => new(stream,
        new(stream == AudioStreamId.LocalMic ? Component.LocalCapture : Component.RemoteCapture, HealthStatus.Healthy, now, null),
        stream == AudioStreamId.LocalMic ? 0.25 : 0.5, 0, 0);
    internal static TranscriptEvent Transcript(Guid session, Guid turn, AudioStreamId stream, bool final, string text)
    {
        TranscriptEvent item = final ? new TranscriptFinal()
        {
            SchemaVersion = 1,
            EventId = Guid.NewGuid(),
            SessionId = session,
            ReceivedUtc = DateTimeOffset.UnixEpoch,
            TurnId = turn,
            StreamId = stream,
            CapturedStartMs = 100,
            CapturedEndMs = 300,
            ProviderItemId = "item",
            Text = text,
            Attribution = stream == AudioStreamId.LocalMic ? Attribution.You : Attribution.Remote,
            AttributionConfidence = AttributionConfidence.SourceConfirmed,
            ConfidenceNote = null
        }
            : new TranscriptPartial()
            {
                SchemaVersion = 1,
                EventId = Guid.NewGuid(),
                SessionId = session,
                ReceivedUtc = DateTimeOffset.UnixEpoch,
                TurnId = turn,
                StreamId = stream,
                CapturedStartMs = 100,
                CapturedEndMs = 300,
                ProviderItemId = "item",
                Text = text,
                Attribution = stream == AudioStreamId.LocalMic ? Attribution.You : Attribution.Remote,
                AttributionConfidence = AttributionConfidence.SourceConfirmed,
                ConfidenceNote = null
            };
        return item;
    }
    private sealed class Pixels(long ticks) : ISnapshotBackend
    {
        public Task<CapturedPixels> CaptureAsync(SnapshotTarget target, CancellationToken token) => Task.FromResult(new CapturedPixels([20, 40, 60, 255], 1, 1, ticks));
    }
}

internal sealed class FakeTime : IMeetingTime
{
    public long Milliseconds = 1000;
    public ClockMapping CreateClock() => new(0, 1000, DateTimeOffset.UnixEpoch);
    public long Now(ClockMapping clock) => Milliseconds;
}
internal sealed class Fixture
{
    public ProcessIdentity Identity = new(101, 100, "C:\\Zoom.exe");
    public FakeTime Time = new();
    public FakeInventory Inventory;
    public FakeRuntimeFactory Factory;
    public MeetingChoice? Choice;
    public Fixture()
    {
        Inventory = new(new(26200, true, [new(101, 0, "Zoom.exe", Identity, 1)],
            [new("mic", "Synthetic microphone", true), new("render", "Synthetic render", false)],
            [new("render", "call", Identity, RenderSessionState.Active, false, false)], []));
        Factory = new(Time);
    }
    public MeetingCoordinator Create(SessionOptions? options = null) => new(Inventory, Factory, Time, options);
    public async Task<MeetingChoice> Choose(MeetingCoordinator coordinator)
    {
        var sources = await coordinator.DiscoverAsync(CallSourceKind.Zoom, default);
        Choice = new(CallSourceKind.Zoom, sources.Applications.Single(), sources.Microphones.Single());
        return Choice;
    }
}
internal sealed class FakeInventory(CallSourceInventory snapshot) : ICallSourceInventory
{
    public CallSourceInventory Snapshot = snapshot;
    public List<FakeLease> Leases = [];
    public TaskCompletionSource? ReadGate;
    public TaskCompletionSource? ReleaseBlockedRead;
    public bool FailBlockedRead;
    public bool Waiting;
    public int ConcurrentReads;
    public async Task<CallSourceInventory> ReadAsync(CancellationToken token)
    {
        Interlocked.Increment(ref ConcurrentReads);
        try
        {
            if (ReadGate is not null)
            {
                ReleaseBlockedRead = ReadGate;
                Waiting = true;
                await ReleaseBlockedRead.Task.WaitAsync(token);
                if (FailBlockedRead) throw new IOException("synthetic stale probe error");
            }
            return Snapshot;
        }
        finally { Interlocked.Decrement(ref ConcurrentReads); }
    }
    public IProcessIdentityLease Pin(ProcessIdentity identity)
    {
        if (Snapshot.Processes.Single().Identity != identity) throw new CallSourceSelectionException("recycled");
        var lease = new FakeLease();
        Leases.Add(lease);
        return lease;
    }
}
internal sealed class FakeLease : IProcessIdentityLease
{
    public bool Alive = true;
    public int Disposals;
    public bool IsAlive => Alive && Disposals == 0;
    public void Dispose() => Interlocked.Increment(ref Disposals);
}
internal sealed class FakeRuntimeFactory(FakeTime time) : IMeetingRuntimeFactory
{
    public List<FakeRuntime> Created = [];
    public bool ReadyOnStart = true;
    public FakeRuntime Latest => Created.Last();
    public IMeetingRuntime Create(Guid sessionId, ProviderMode mode, Action<CaptureMessage> observe)
    {
        var runtime = new FakeRuntime(sessionId, mode, observe, time, ReadyOnStart);
        Created.Add(runtime);
        return runtime;
    }
}
internal sealed class FakeRuntime(Guid id, ProviderMode mode, Action<CaptureMessage> observe, FakeTime time, bool ready) : IMeetingRuntime
{
    private readonly Channel<ConversationEvent> events = Channel.CreateUnbounded<ConversationEvent>();
    public int Disposals;
    public bool ShutdownFailure;
    public TaskCompletionSource? DisposeGate;
    public CaptureSelection? Selection;
    public ClockMapping? Clock;
    public ComponentHealth[] Transport = [];
    public void Observe(CaptureMessage message) => observe(message);
    public void Emit(ConversationEvent item) => events.Writer.TryWrite(item);
    public async Task RunAsync(CaptureSelection selection, ClockMapping clock, CancellationToken token)
    {
        Selection = selection;
        Clock = clock;
        if (ready)
        {
            observe(CoordinatorTests.Healthy(AudioStreamId.LocalMic, time.Milliseconds));
            observe(CoordinatorTests.Healthy(AudioStreamId.RemoteApp, time.Milliseconds));
            if (mode != ProviderMode.Unavailable) Emit(new SessionStateEvent
            {
                SchemaVersion = 1,
                EventId = Guid.NewGuid(),
                SessionId = id,
                ReceivedUtc = DateTimeOffset.UnixEpoch,
                State = SessionState.Listening,
                Health = [new(Component.LocalStt, HealthStatus.Healthy, time.Milliseconds, null), new(Component.RemoteStt, HealthStatus.Healthy, time.Milliseconds, null)]
            });
        }
        try { await Task.Delay(Timeout.Infinite, token); }
        finally { events.Writer.TryComplete(); if (ShutdownFailure) throw new IOException("synthetic graceful shutdown failure"); }
    }
    public IAsyncEnumerable<ConversationEvent> ReadAllAsync(CancellationToken token) => events.Reader.ReadAllAsync(token);
    public ComponentHealth[] SampleTransportHealth(long nowMs) => Transport.Select(h => h with { ObservedAtMs = nowMs }).ToArray();
    public async ValueTask DisposeAsync()
    {
        if (DisposeGate is not null) await DisposeGate.Task;
        Interlocked.Increment(ref Disposals);
    }
}
