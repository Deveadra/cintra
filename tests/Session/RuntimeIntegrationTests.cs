using System.IO;
using System.Net.WebSockets;
using System.Threading.Channels;
using MeetingCompanion.Contracts;
using MeetingCompanion.Session;
using MeetingCompanion.Stt;

namespace MeetingCompanion.Session.Tests;

public sealed class RuntimeIntegrationTests
{
    [Fact]
    public void RuntimeConstructionFailureDisposesItsUnstartedCaptureOwner()
    {
        var capture = new FakeCapture(new());
        var factory = new MeetingRuntimeFactory("unused", () => false, () => null, () => capture,
            _ => throw new InvalidOperationException("synthetic factory failure"));
        Assert.Throws<InvalidOperationException>(() => factory.Create(Guid.NewGuid(), ProviderMode.OfflineTest, _ => { }));
        Assert.Equal(1, capture.Disposals);
        Assert.Null(capture.Clock);
    }
    [Fact]
    public async Task ActualDualStreamTranscriberUsesFakeSocketsWithFreshBuffersAfterPause()
    {
        var fixture = new Fixture();
        var captures = new List<FakeCapture>();
        int keyReads = 0;
        var factory = new MeetingRuntimeFactory("unused", () => false, () => { keyReads++; throw new InvalidOperationException(); },
            () => { var capture = new FakeCapture(fixture.Time); captures.Add(capture); return capture; });
        await using var coordinator = new MeetingCoordinator(fixture.Inventory, factory, fixture.Time);
        await coordinator.StartAsync(await fixture.Choose(coordinator), ProviderMode.OfflineTest, default);
        Assert.Equal(SessionState.Listening, coordinator.View.State);
        var first = captures.Single();
        Assert.NotEqual(first.SessionId, coordinator.View.SessionId);
        foreach (var stream in new[] { AudioStreamId.LocalMic, AudioStreamId.RemoteApp })
            first.VoiceTurn(stream, 1000);
        fixture.Time.Milliseconds = 2000;
        await CoordinatorTests.Until(() => coordinator.View.Transcripts.OfType<TranscriptFinal>().Count() == 2);
        Assert.Contains(coordinator.View.Transcripts, t => t.Attribution == Attribution.You);
        Assert.Contains(coordinator.View.Transcripts, t => t.Attribution == Attribution.Remote);
        Assert.All(coordinator.View.Transcripts, t => Assert.StartsWith("TEST MODE", t.Text));
        await coordinator.PollAsync(default);
        await coordinator.PauseAsync(default);
        Assert.Equal(1, first.Disposals);
        var finals = coordinator.View.Transcripts.Length;
        first.VoiceTurn(AudioStreamId.LocalMic, 2000);
        await coordinator.ResumeAsync(default);
        Assert.Equal(2, captures.Count);
        Assert.Equal(finals, coordinator.View.Transcripts.Length);
        Assert.Same(first.Clock, captures[1].Clock);
        captures[1].VoiceTurn(AudioStreamId.RemoteApp, 2000);
        fixture.Time.Milliseconds = 3000;
        await CoordinatorTests.Until(() => coordinator.View.Transcripts.OfType<TranscriptFinal>().Count() == 3);
        await coordinator.StopAsync();
        Assert.All(captures, capture => Assert.Equal(1, capture.Disposals));
        Assert.All(captures, capture => Assert.Equal(1, capture.Stops));
        Assert.Equal(0, keyReads);
        Assert.Empty(coordinator.View.Transcripts);
    }

    [Fact]
    public async Task ActualCaptureOnlyRuntimeReleasesCaptureWithoutConstructingSocketsOrReadingCredentials()
    {
        var fixture = new Fixture();
        var capture = new FakeCapture(fixture.Time);
        var factory = new MeetingRuntimeFactory("unused", () => throw new InvalidOperationException(),
            () => throw new InvalidOperationException(), () => capture, _ => throw new InvalidOperationException());
        await using var coordinator = new MeetingCoordinator(fixture.Inventory, factory, fixture.Time);
        await coordinator.StartAsync(await fixture.Choose(coordinator), ProviderMode.Unavailable, default);
        Assert.Equal(SessionState.Degraded, coordinator.View.State);
        await coordinator.StopAsync();
        Assert.Equal(1, capture.Stops);
        Assert.Equal(1, capture.Disposals);
    }

    [Fact]
    public async Task ActualSocketFailureOnOneSideFailsStartupAndReleasesNativeOwner()
    {
        var fixture = new Fixture();
        var capture = new FakeCapture(fixture.Time);
        var factory = new MeetingRuntimeFactory("unused", () => false, () => throw new InvalidOperationException(), () => capture,
            stream => stream == AudioStreamId.LocalMic ? new ScriptedSocketFactory("TEST MODE") : new FailingFactory(),
            new() { MaxReconnectAttempts = 0 });
        await using var coordinator = new MeetingCoordinator(fixture.Inventory, factory, fixture.Time);
        var choice = await fixture.Choose(coordinator);
        await Assert.ThrowsAsync<IOException>(() => coordinator.StartAsync(choice, ProviderMode.OfflineTest, default));
        Assert.Equal(SessionState.Error, coordinator.View.State);
        Assert.Equal(1, capture.Disposals);
        Assert.Equal(1, fixture.Inventory.Leases.Single().Disposals);
    }

    [Fact]
    public async Task ZeroLevelSourceIsUsableButNeverHealthyListening()
    {
        var fixture = new Fixture();
        var capture = new FakeCapture(fixture.Time) { InitialStatus = HealthStatus.ZeroLevel };
        var factory = new MeetingRuntimeFactory("unused", () => false, () => null, () => capture);
        await using var coordinator = new MeetingCoordinator(fixture.Inventory, factory, fixture.Time);
        await coordinator.StartAsync(await fixture.Choose(coordinator), ProviderMode.OfflineTest, default);
        Assert.Equal(SessionState.Degraded, coordinator.View.State);
        Assert.All(coordinator.View.Health.Where(h => h.Component is Component.LocalCapture or Component.RemoteCapture), h => Assert.Equal(HealthStatus.ZeroLevel, h.Status));
    }

    private sealed class FailingFactory : IRealtimeSocketFactory
    {
        public IRealtimeSocket Create() => new FailedSocket();
        private sealed class FailedSocket : IRealtimeSocket
        {
            public Task ConnectAsync(Uri endpoint, string key, CancellationToken token) => Task.FromException(new WebSocketException("synthetic socket failure"));
            public Task SendTextAsync(string json, CancellationToken token) => throw new InvalidOperationException();
            public Task<SocketRead> ReceiveAsync(Memory<byte> buffer, CancellationToken token) => throw new InvalidOperationException();
            public Task CloseAsync(CancellationToken token) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
internal sealed class FakeCapture(FakeTime time) : IAudioCaptureSession
{
    private readonly Channel<CaptureMessage> messages = Channel.CreateUnbounded<CaptureMessage>();
    public Guid SessionId { get; } = Guid.NewGuid();
    public ClockMapping? Clock;
    public int Stops;
    public int Disposals;
    public HealthStatus InitialStatus = HealthStatus.Healthy;
    public Task StartAsync(CaptureSelection selection, ClockMapping clock, CancellationToken token)
    {
        Clock = clock;
        foreach (var stream in new[] { AudioStreamId.LocalMic, AudioStreamId.RemoteApp })
            messages.Writer.TryWrite(CoordinatorTests.Healthy(stream, time.Milliseconds) with
            { Health = new(stream == AudioStreamId.LocalMic ? Component.LocalCapture : Component.RemoteCapture, InitialStatus, time.Milliseconds, null) });
        return Task.CompletedTask;
    }
    public void VoiceTurn(AudioStreamId stream, long start)
    {
        for (int i = 0; i < 30; i++)
        {
            var pcm = new byte[960];
            if (i < 5)
                for (int p = 0; p < pcm.Length; p += 2) { pcm[p] = 0; pcm[p + 1] = 20; }
            messages.Writer.TryWrite(new AudioFrame(stream, i, start + i * 20, start + (i + 1) * 20, AudioFormat.Normalized, 24000, pcm));
        }
    }
    public IAsyncEnumerable<CaptureMessage> ReadAllAsync(CancellationToken token) => messages.Reader.ReadAllAsync(token);
    public Task PauseAsync(CancellationToken token) => throw new InvalidOperationException("Coordinator must release runtime on Pause.");
    public Task ResumeAsync(CancellationToken token) => throw new InvalidOperationException("Coordinator must recreate runtime on Resume.");
    public Task StopAsync(CancellationToken token) { Stops++; messages.Writer.TryComplete(); return Task.CompletedTask; }
    public ValueTask DisposeAsync() { Disposals++; messages.Writer.TryComplete(); return ValueTask.CompletedTask; }
}
