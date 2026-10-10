using System.Threading.Channels;
using MeetingCompanion.Contracts;

namespace MeetingCompanion.Stt;

/// <summary>Owns native capture consumption and two independent STT transports.</summary>
public sealed class DualStreamTranscriber : IAsyncDisposable
{
    private readonly IAudioCaptureSession capture;
    private readonly RealtimeTranscriptionSession local;
    private readonly RealtimeTranscriptionSession remote;
    private readonly Channel<ConversationEvent> events = Channel.CreateBounded<ConversationEvent>(
        new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = false });
    private readonly object healthGate = new();
    private readonly Dictionary<Component, ComponentHealth> health = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource runFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool running;
    private bool disposed;

    public DualStreamTranscriber(IAudioCaptureSession capture,
        Func<AudioStreamId, IRealtimeSocketFactory> socketFactories,
        Func<string?> apiKeyProvider, SttOptions? options = null)
    {
        this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
        ArgumentNullException.ThrowIfNull(socketFactories);
        local = new RealtimeTranscriptionSession(capture.SessionId, AudioStreamId.LocalMic,
            socketFactories(AudioStreamId.LocalMic), apiKeyProvider, options);
        remote = new RealtimeTranscriptionSession(capture.SessionId, AudioStreamId.RemoteApp,
            socketFactories(AudioStreamId.RemoteApp), apiKeyProvider, options);
    }

    public IAsyncEnumerable<ConversationEvent> ReadAllAsync(CancellationToken cancellationToken) =>
        events.Reader.ReadAllAsync(cancellationToken);

    public async Task RunAsync(CaptureSelection selection, ClockMapping clock, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (running) throw new InvalidOperationException("Transcriber is single-use.");
        selection.Validate();
        clock.Validate();
        if (selection.Incoming.StreamId != AudioStreamId.RemoteApp)
            throw new NotSupportedException("MC-006 requires an isolated remote application source.");
        running = true;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        Task? localEvents = null;
        Task? remoteEvents = null;
        try
        {
            await capture.StartAsync(selection, clock, linked.Token).ConfigureAwait(false);
            localEvents = ForwardEventsAsync(local, linked.Token);
            remoteEvents = ForwardEventsAsync(remote, linked.Token);
            var starts = new[] { local.StartAsync(linked.Token), remote.StartAsync(linked.Token) };
            await Task.WhenAll(starts).ConfigureAwait(false);
            await foreach (var message in capture.ReadAllAsync(linked.Token).ConfigureAwait(false))
            {
                message.Validate();
                switch (message)
                {
                    case AudioFrame { StreamId: AudioStreamId.LocalMic } mic:
                        await local.AppendAsync(mic, linked.Token).ConfigureAwait(false);
                        break;
                    case AudioFrame { StreamId: AudioStreamId.RemoteApp } incoming:
                        await remote.AppendAsync(incoming, linked.Token).ConfigureAwait(false);
                        break;
                    case CaptureGap gap:
                        SessionFor(gap.StreamId).MarkCaptureGap(gap);
                        break;
                    case CaptureSourceChanged changed:
                        SessionFor(changed.Source.StreamId).MarkCaptureGap(new CaptureGap(
                            changed.Source.StreamId, changed.EffectiveAtMs, changed.EffectiveAtMs,
                            GapReason.SourceChanged, 0));
                        await PublishHealthAsync(new ComponentHealth(
                            changed.Source.StreamId == AudioStreamId.LocalMic ? Component.LocalCapture : Component.RemoteCapture,
                            HealthStatus.Disconnected, changed.EffectiveAtMs, "source_changed"), linked.Token).ConfigureAwait(false);
                        var sourceEvent = new SourceChangedEvent
                        {
                            SchemaVersion = ContractVersion.Current,
                            EventId = Guid.NewGuid(),
                            SessionId = capture.SessionId,
                            ReceivedUtc = DateTimeOffset.UtcNow,
                            Change = changed
                        };
                        sourceEvent.Validate();
                        await events.Writer.WriteAsync(sourceEvent, linked.Token).ConfigureAwait(false);
                        break;
                    case CaptureHealth health:
                        await PublishHealthAsync(health.Health, linked.Token).ConfigureAwait(false);
                        if (health.Health.Status is HealthStatus.NoDevice or HealthStatus.Disconnected or
                            HealthStatus.Failed or HealthStatus.PermissionDenied or HealthStatus.WrongProcess)
                            SessionFor(health.StreamId).MarkCaptureGap(new CaptureGap(health.StreamId,
                                health.Health.ObservedAtMs, health.Health.ObservedAtMs, GapReason.DeviceLost, 0));
                        break;
                }
            }
        }
        finally
        {
            try
            {
                linked.Cancel();
                try { await capture.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                finally
                {
                    try { await local.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                    finally
                    {
                        try { await remote.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                        finally
                        {
                            if (localEvents is not null) try { await localEvents.ConfigureAwait(false); } catch (OperationCanceledException) { }
                            if (remoteEvents is not null) try { await remoteEvents.ConfigureAwait(false); } catch (OperationCanceledException) { }
                        }
                    }
                }
            }
            finally
            {
                events.Writer.TryComplete();
                runFinished.TrySetResult();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        lifetime.Cancel();
        if (running) await runFinished.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await local.DisposeAsync().ConfigureAwait(false);
        await remote.DisposeAsync().ConfigureAwait(false);
        await capture.DisposeAsync().ConfigureAwait(false);
        events.Writer.TryComplete();
        lifetime.Dispose();
        disposed = true;
    }

    private RealtimeTranscriptionSession SessionFor(AudioStreamId streamId) => streamId switch
    {
        AudioStreamId.LocalMic => local,
        AudioStreamId.RemoteApp => remote,
        _ => throw new ContractException("Unexpected stream in dual-source capture.")
    };

    private async Task ForwardEventsAsync(ITranscriptionSession session, CancellationToken cancellationToken)
    {
        await foreach (var output in session.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (output is SessionStateEvent state)
                foreach (var component in state.Health)
                    await PublishHealthAsync(component, cancellationToken).ConfigureAwait(false);
            else
                await events.Writer.WriteAsync(output, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishHealthAsync(ComponentHealth update, CancellationToken cancellationToken)
    {
        ComponentHealth[] snapshot;
        SessionState state;
        lock (healthGate)
        {
            health[update.Component] = update;
            snapshot = health.Values.ToArray();
            var required = new[] { Component.LocalCapture, Component.RemoteCapture, Component.LocalStt, Component.RemoteStt };
            var allHealthy = required.All(x => health.TryGetValue(x, out var entry) &&
                entry.Status is HealthStatus.Healthy or HealthStatus.Silence);
            var degraded = snapshot.Any(x => x.Status is HealthStatus.NoDevice or HealthStatus.WrongProcess or
                HealthStatus.PermissionDenied or HealthStatus.Disconnected or HealthStatus.Reconnecting or HealthStatus.Failed);
            state = allHealthy ? SessionState.Listening : degraded ? SessionState.Degraded : SessionState.Starting;
        }
        var output = new SessionStateEvent
        {
            SchemaVersion = ContractVersion.Current,
            EventId = Guid.NewGuid(),
            SessionId = capture.SessionId,
            ReceivedUtc = DateTimeOffset.UtcNow,
            State = state,
            Health = snapshot
        };
        output.Validate();
        await events.Writer.WriteAsync(output, cancellationToken).ConfigureAwait(false);
    }
}
