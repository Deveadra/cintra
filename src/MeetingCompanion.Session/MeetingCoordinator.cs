using MeetingCompanion.Contracts;
using MeetingCompanion.Core;
using MeetingCompanion.Platform;

namespace MeetingCompanion.Session;

/// <summary>Serializes side effects; short data locks fence late events from previous runtime generations.</summary>
public sealed class MeetingCoordinator : IAsyncDisposable
{
    private readonly SemaphoreSlim lifecycle = new(1);
    private readonly object gate = new();
    private readonly ICallSourceInventory inventory;
    private readonly IMeetingRuntimeFactory runtimes;
    private readonly IMeetingTime time;
    private readonly SessionOptions options;
    private readonly Dictionary<CallSourceKind, WindowsCallSourceAdapter> adapters;
    private readonly SemaphoreSlim probe = new(1);
    private MeetingChoice? choice;
    private SelectedCallSource? lease;
    private RollingConversationStore? store;
    private IMeetingRuntime? runtime;
    private CancellationTokenSource? active;
    private Task? run;
    private Task? pump;
    private Task? cleanup;
    private volatile bool cleanupReleased;
    private TaskCompletionSource ready = NewCompletion();
    private ClockMapping? clock;
    private Guid? sessionId;
    private long generation;
    private long revision;
    private bool disposed;
    private bool stopRequested;
    private bool rediscoveryRequired;
    private SessionState state = SessionState.Stopped;
    private ProviderMode provider;
    private string detail = "Stopped — select an application and microphone.";
    private string? lastSource;
    private readonly Dictionary<Component, ComponentHealth> health = [];
    private readonly Dictionary<Component, ComponentHealth> providerReports = [];
    private readonly Dictionary<AudioStreamId, long> lastGoodCapture = [];
    private long lastSourceValidatedAtMs;
    private readonly Queue<AudioGapEvent> failureGaps = new();
    private SourceChangedEvent? failureSource;
    private double localPeak;
    private double remotePeak;
    private SnapshotHandoff? snapshot;

    public MeetingCoordinator(ICallSourceInventory inventory, IMeetingRuntimeFactory runtimes,
        IMeetingTime? time = null, SessionOptions? options = null)
    {
        this.inventory = inventory;
        this.runtimes = runtimes;
        this.time = time ?? new MeetingTime();
        this.options = options ?? new();
        if (this.options.StartupDeadline <= TimeSpan.Zero || this.options.CleanupDeadline <= TimeSpan.Zero || this.options.HealthTtlMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        adapters = Enum.GetValues<CallSourceKind>().ToDictionary(k => k, k => new WindowsCallSourceAdapter(inventory, k));
    }
    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long Now => clock is null ? 0 : time.Now(clock);
    public MeetingView View
    {
        get
        {
            lock (gate)
            {
                var previousState = state;
                if (state is SessionState.Listening or SessionState.Degraded)
                {
                    ExpireHealth();
                    UpdateState();
                    if (state != previousState) revision++;
                }
                var visible = store?.GetView(Now);
                return new(revision, sessionId, state, provider, detail,
                    health.Values.OrderBy(h => h.Component).ToArray(), localPeak, remotePeak,
                    visible?.Events.OfType<TranscriptEvent>().ToArray() ?? [],
                    (visible?.Events.OfType<AudioGapEvent>() ?? []).Concat(failureGaps).ToArray(),
                    (visible?.Events.OfType<SourceChangedEvent>() ?? []).Concat(failureSource is null ? [] : new[] { failureSource }).ToArray(), snapshot?.Metadata, lastSource);
            }
        }
    }

    public async Task<SourceChoices> DiscoverAsync(CallSourceKind kind, CancellationToken token)
    {
        await lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (lease is not null || runtime is not null || cleanup is not null)
                throw new InvalidOperationException("Stop before refreshing sources.");
            var apps = await adapters[kind].DiscoverAsync(token).ConfigureAwait(false);
            var fresh = await inventory.ReadAsync(token).ConfigureAwait(false);
            rediscoveryRequired = false;
            return new(apps.ToArray(), fresh.Endpoints.Where(e => e.IsCapture).ToArray(), fresh.Diagnostics);
        }
        finally { lifecycle.Release(); }
    }

    public async Task StartAsync(MeetingChoice selected, ProviderMode mode, CancellationToken token)
    {
        await lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (lease is not null || runtime is not null || cleanup is not null)
                throw new InvalidOperationException("Stop and finish cleanup before starting again.");
            if (rediscoveryRequired) throw new CallSourceSelectionException("rediscover_required");
            lock (gate)
            {
                choice = selected;
                failureGaps.Clear();
                failureSource = null;
                stopRequested = false;
                provider = mode;
                state = SessionState.Starting;
                detail = "Validating fresh source identity and microphone…";
                active = CancellationTokenSource.CreateLinkedTokenSource(token);
                revision++;
            }
            using var deadline = new CancellationTokenSource(options.StartupDeadline);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(active.Token, deadline.Token);
            try
            {
                var fresh = await inventory.ReadAsync(startup.Token).ConfigureAwait(false);
                if (!fresh.AudioInventoryComplete || !fresh.Endpoints.Any(e => e == selected.Microphone && e.IsCapture))
                    throw new CallSourceSelectionException("microphone_missing_or_inventory_incomplete");
                lease = await adapters[selected.Kind].AcquireAsync(selected.Candidate, startup.Token).ConfigureAwait(false);
                lease.EnsureAlive();
                lock (gate)
                {
                    sessionId = Guid.NewGuid();
                    clock = time.CreateClock();
                    lastSourceValidatedAtMs = Now;
                    store = new RollingConversationStore(sessionId.Value, clock);
                    lastSource = $"{selected.Candidate.AppDisplayName} · PID {selected.Candidate.ProcessId} · {selected.Microphone.DisplayName}";
                }
                await StartRuntimeAsync(startup.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                bool timedOut = deadline.IsCancellationRequested && !token.IsCancellationRequested && !stopRequested;
                await StopCoreAsync("Startup cancelled or failed; select sources and retry.", true).ConfigureAwait(false);
                if (timedOut) throw new TimeoutException("Session startup deadline expired.", error);
                throw;
            }
        }
        finally { lifecycle.Release(); }
    }

    private async Task StartRuntimeAsync(CancellationToken token)
    {
        lease!.EnsureAlive(); // The identity stays pinned through the native startup handshake and cleanup.
        long epoch;
        lock (gate)
        {
            epoch = ++generation;
            ready = NewCompletion();
            health.Clear();
            providerReports.Clear();
            lastGoodCapture.Clear();
            active ??= CancellationTokenSource.CreateLinkedTokenSource(token);
            state = SessionState.Starting;
            detail = provider switch
            {
                ProviderMode.OfflineTest => "TEST MODE — real capture; scripted provider text is not recognition.",
                ProviderMode.Unavailable => "Transcription unavailable — configure key and explicitly enable paid provider.",
                _ => "Connecting two independent transcription sessions…"
            };
            revision++;
        }
        runtime = runtimes.Create(sessionId!.Value, provider, message => Observe(epoch, message));
        var current = runtime;
        var cancellation = active;
        pump = Task.Run(() => PumpAsync(epoch, current, cancellation.Token));
        run = Task.Run(async () =>
        {
            try
            {
                await current.RunAsync(new(new(AudioStreamId.LocalMic, choice!.Microphone.DisplayName,
                    choice.Microphone.Id, null, false, false), lease.Resolved.Source), clock!, cancellation.Token).ConfigureAwait(false);
                if (!cancellation.IsCancellationRequested) Fault(epoch, "capture_pipeline_ended");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                lock (gate)
                    if (!stopRequested && epoch == generation && state is SessionState.Listening or SessionState.Degraded)
                        Fault(epoch, "session_cancelled");
            }
            catch { Fault(epoch, "capture_or_provider_failed"); throw; }
        });
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token, cancellation.Token);
        try { await ready.Task.WaitAsync(options.StartupDeadline, waiting.Token).ConfigureAwait(false); }
        catch { cancellation.Cancel(); throw; }
    }

    private async Task PumpAsync(long epoch, IMeetingRuntime current, CancellationToken token)
    {
        try
        {
            await foreach (var item in current.ReadAllAsync(token).ConfigureAwait(false))
            {
                lock (gate)
                {
                    if (epoch != generation || token.IsCancellationRequested || sessionId != item.SessionId) continue;
                    item.Validate();
                    if (item is SessionStateEvent status)
                    {
                        // DualStreamTranscriber snapshots may repeat older capture entries. Only STT deltas live here.
                        foreach (var h in status.Health.Where(h => h.Component is Component.LocalStt or Component.RemoteStt))
                        {
                            if (providerReports.TryGetValue(h.Component, out var previous) && previous == h) continue;
                            providerReports[h.Component] = h;
                            health[h.Component] = h with { ObservedAtMs = Now };
                        }
                        UpdateState();
                    }
                    else store!.Apply(item);
                    revision++;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { Fault(epoch, "event_pipeline_failed"); throw; }
    }

    private void Observe(long epoch, CaptureMessage message)
    {
        lock (gate)
        {
            if (epoch != generation || active?.IsCancellationRequested != false) return;
            message.Validate();
            switch (message)
            {
                case CaptureHealth h:
                    health[h.Health.Component] = h.Health;
                    if (h.Health.Status is HealthStatus.Healthy or HealthStatus.Silence or HealthStatus.ZeroLevel && h.Health.ObservedAtMs <= Now)
                        lastGoodCapture[h.StreamId] = h.Health.ObservedAtMs;
                    if (h.StreamId == AudioStreamId.LocalMic) localPeak = h.Peak; else remotePeak = h.Peak;
                    if (h.Health.Status is HealthStatus.NoDevice or HealthStatus.WrongProcess or HealthStatus.PermissionDenied or HealthStatus.Disconnected or HealthStatus.Failed)
                        Fault(epoch, h.Health.DiagnosticCode ?? "capture_failed",
                            h.Health.DiagnosticCode?.Contains("helper", StringComparison.Ordinal) == true ? GapReason.HelperExited : GapReason.DeviceLost);
                    break;
                case CaptureSourceChanged changed:
                    failureSource = new()
                    {
                        SchemaVersion = 1,
                        EventId = Guid.NewGuid(),
                        SessionId = sessionId!.Value,
                        ReceivedUtc = clock!.UtcOrigin.AddMilliseconds(Now),
                        Change = changed
                    };
                    Fault(epoch, "source_changed_choose_again", GapReason.SourceChanged);
                    break;
            }
            UpdateState();
            revision++;
        }
    }

    private void UpdateState()
    {
        if (state is SessionState.Error or SessionState.Stopping or SessionState.Stopped or SessionState.Paused) return;
        var captureReady = new[] { Component.LocalCapture, Component.RemoteCapture }.All(FreshHealthy);
        var captureAvailable = new[] { Component.LocalCapture, Component.RemoteCapture }.All(c => health.TryGetValue(c, out var h) &&
            h.Status is HealthStatus.Healthy or HealthStatus.Silence or HealthStatus.ZeroLevel && h.ObservedAtMs <= Now && Now - h.ObservedAtMs < options.HealthTtlMs);
        var allReady = captureReady && new[] { Component.LocalStt, Component.RemoteStt }.All(FreshHealthy);
        state = allReady ? SessionState.Listening : SessionState.Degraded;
        if (captureAvailable && (new[] { Component.LocalStt, Component.RemoteStt }.All(FreshHealthy) || provider == ProviderMode.Unavailable)) ready.TrySetResult();
        var problem = health.Values.FirstOrDefault(h => h.Status is HealthStatus.Failed or HealthStatus.PermissionDenied);
        if (problem is not null && provider != ProviderMode.Unavailable) ready.TrySetException(new IOException("Component unavailable: " + problem.Component));
        if (provider == ProviderMode.Unavailable)
        {
            foreach (var component in new[] { Component.LocalStt, Component.RemoteStt })
                health[component] = new(component, HealthStatus.Failed, Now, "provider_not_enabled");
        }
    }
    private bool FreshHealthy(Component component) => health.TryGetValue(component, out var h) &&
        h.Status is HealthStatus.Healthy or HealthStatus.Silence && h.ObservedAtMs <= Now && Now - h.ObservedAtMs < options.HealthTtlMs;

    private void Fault(long epoch, string code, GapReason reason = GapReason.Unknown)
    {
        lock (gate)
        {
            if (epoch != generation || state is SessionState.Stopping or SessionState.Stopped or SessionState.Error) return;
            state = SessionState.Error;
            rediscoveryRequired = true;
            if (sessionId is { } id && clock is not null)
                foreach (var stream in new[] { AudioStreamId.LocalMic, AudioStreamId.RemoteApp })
                {
                    long last = lastGoodCapture.GetValueOrDefault(stream, Now);
                    if (reason == GapReason.SourceChanged) last = Math.Min(last, lastSourceValidatedAtMs);
                    failureGaps.Enqueue(new()
                    {
                        SchemaVersion = 1,
                        EventId = Guid.NewGuid(),
                        SessionId = id,
                        ReceivedUtc = clock.UtcOrigin.AddMilliseconds(Now),
                        Gap = new(stream, Math.Min(last, Now), Now, reason, 0)
                    });
                    while (failureGaps.Count > 4) failureGaps.Dequeue();
                }
            detail = code + " — capture stopped; choose sources again.";
            ready.TrySetException(new IOException(code));
            active?.Cancel();
            revision++;
        }
        _ = StopAfterFaultAsync(code);
    }
    private async Task StopAfterFaultAsync(string code)
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (gate) { if (state != SessionState.Error || !detail.StartsWith(code, StringComparison.Ordinal)) return; }
            try { await StopCoreAsync(code + " — uncertainty marked; select sources again.", true).ConfigureAwait(false); }
            catch { /* StopCore exposes cleanup failure and prevents restart/closure. */ }
        }
        finally { lifecycle.Release(); }
    }

    /// <summary>Caller schedules this periodically; a concurrent probe is skipped, never overlapped.</summary>
    public async Task PollAsync(CancellationToken token)
    {
        if (token.IsCancellationRequested || !await probe.WaitAsync(0).ConfigureAwait(false)) return;
        long epoch = -1;
        try
        {
            MeetingChoice? selected;
            lock (gate)
            {
                selected = choice;
                epoch = generation;
                if (lease is null || state is not (SessionState.Listening or SessionState.Degraded or SessionState.Starting or SessionState.Paused)) return;
                if (!lease.IsAlive) { Fault(epoch, "selected_process_exited", GapReason.SourceChanged); return; }
            }
            var validity = await adapters[selected!.Kind].ValidateAsync(selected.Candidate, token).ConfigureAwait(false);
            lock (gate)
            {
                if (epoch != generation || lease is null) return;
                if (!validity.CanSelect) { Fault(epoch, validity.DiagnosticCode, GapReason.SourceChanged); return; }
                lastSourceValidatedAtMs = Now;
                if (state == SessionState.Paused) return;
                foreach (var h in runtime?.SampleTransportHealth(Now) ?? [])
                    if (!health.TryGetValue(h.Component, out var previous) || previous.Status is not (HealthStatus.Failed or HealthStatus.PermissionDenied))
                        health[h.Component] = h;
                ExpireHealth();
                UpdateState();
                revision++;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { Fault(epoch, "source_validation_failed"); }
        finally { probe.Release(); }
    }

    private void ExpireHealth()
    {
        foreach (var key in health.Keys.ToArray())
            if (Now - health[key].ObservedAtMs >= options.HealthTtlMs && health[key].Status is HealthStatus.Healthy or HealthStatus.Silence or HealthStatus.ZeroLevel)
            {
                health[key] = health[key] with { Status = HealthStatus.Unknown, DiagnosticCode = "stale_health" };
                if (key == Component.LocalCapture) localPeak = 0;
                if (key == Component.RemoteCapture) remotePeak = 0;
                revision++;
            }
    }

    public async Task PauseAsync(CancellationToken token)
    {
        await lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (gate)
            {
                if (state is not (SessionState.Listening or SessionState.Degraded)) throw new InvalidOperationException("Session is not active.");
                MarkGaps(GapReason.Paused);
                state = SessionState.Stopping;
                generation++;
                active?.Cancel();
                ClearSnapshotCore();
                revision++;
            }
            await CleanupRuntimeAsync().ConfigureAwait(false);
            lock (gate)
            {
                state = SessionState.Paused;
                detail = "Paused — native capture and WebSockets released. Resume starts fresh voice buffers.";
                health.Clear();
                localPeak = remotePeak = 0;
                revision++;
            }
        }
        catch { lock (gate) { state = SessionState.Error; detail = "Pause cleanup failed — Stop must finish before restart or closure."; revision++; } throw; }
        finally { lifecycle.Release(); }
    }

    public async Task ResumeAsync(CancellationToken token)
    {
        await lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (state != SessionState.Paused) throw new InvalidOperationException("Session is not paused.");
            var validity = await adapters[choice!.Kind].ValidateAsync(choice.Candidate, token).ConfigureAwait(false);
            if (!validity.CanSelect) throw new CallSourceSelectionException(validity.DiagnosticCode);
            lock (gate) lastSourceValidatedAtMs = Now;
            await StartRuntimeAsync(token).ConfigureAwait(false);
        }
        catch { await StopCoreAsync("Resume failed — select sources again.", true).ConfigureAwait(false); throw; }
        finally { lifecycle.Release(); }
    }

    public async Task StopAsync(CancellationToken token = default)
    {
        // Stop cancels startup BEFORE waiting for the serialized owner. Cancellation cannot skip cleanup.
        lock (gate) { stopRequested = true; active?.Cancel(); }
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync("Stopped — memory cleared and resources released.", false).ConfigureAwait(false); }
        finally { lifecycle.Release(); }
    }

    private async Task StopCoreAsync(string message, bool error)
    {
        lock (gate)
        {
            state = SessionState.Stopping;
            generation++;
            active?.Cancel();
            store?.Clear();
            store = null;
            ClearSnapshotCore();
            health.Clear();
            if (!error) { failureGaps.Clear(); failureSource = null; }
            localPeak = remotePeak = 0;
            revision++;
        }
        try
        {
            await CleanupRuntimeAsync().ConfigureAwait(false);
            // A process identity may be released only after the native helper has finished stopping.
            lease?.Dispose();
            lease = null;
            lock (gate)
            {
                sessionId = null;
                clock = null;
                choice = null;
                state = error ? SessionState.Error : SessionState.Stopped;
                detail = message;
                revision++;
            }
        }
        catch
        {
            if (runtime is null && cleanup is null)
            {
                // Disposal succeeded even though Run reported a failed graceful shutdown.
                lease?.Dispose();
                lease = null;
                lock (gate) { sessionId = null; clock = null; choice = null; }
                if (error)
                {
                    lock (gate) { state = SessionState.Error; detail = message + " Cleanup completed after a pipeline failure."; revision++; }
                    return;
                }
            }
            lock (gate) { state = SessionState.Error; detail = "Cleanup failed or timed out — Stop again; closure and restart are blocked."; revision++; }
            throw;
        }
    }

    private async Task CleanupRuntimeAsync()
    {
        if (cleanup is null && runtime is not null)
        {
            var owned = runtime;
            var tasks = new[] { run, pump }.OfType<Task>().ToArray();
            cleanupReleased = false;
            cleanup = Task.Run(async () =>
            {
                try { await Task.WhenAll(tasks).ConfigureAwait(false); }
                finally { await owned.DisposeAsync().ConfigureAwait(false); cleanupReleased = true; }
            });
        }
        try { if (cleanup is not null) await cleanup.WaitAsync(options.CleanupDeadline).ConfigureAwait(false); }
        catch
        {
            if (cleanupReleased) ReleaseRuntimeReferences();
            throw;
        }
        ReleaseRuntimeReferences();
    }
    private void ReleaseRuntimeReferences()
    {
        cleanup = null;
        runtime = null;
        run = pump = null;
        active?.Dispose();
        active = null;
    }

    private void MarkGaps(GapReason reason)
    {
        if (store is null || sessionId is null) return;
        foreach (var stream in new[] { AudioStreamId.LocalMic, AudioStreamId.RemoteApp })
            store.Apply(new AudioGapEvent
            {
                SchemaVersion = 1,
                EventId = Guid.NewGuid(),
                SessionId = sessionId.Value,
                ReceivedUtc = clock!.UtcOrigin.AddMilliseconds(Now),
                Gap = new(stream, Now, Now, reason, 0)
            });
    }

    public SnapshotTicket? BeginSnapshot()
    {
        lock (gate) return sessionId is { } id && clock is not null && state is SessionState.Listening or SessionState.Degraded or SessionState.Paused
            ? new(id, generation, clock) : null;
    }
    public bool AcceptSnapshot(SnapshotTicket ticket, SnapshotMetadata metadata, byte[] png)
    {
        lock (gate)
        {
            if (ticket.SessionId != sessionId || ticket.Generation != generation || ticket.Clock != clock || store is null ||
                state is not (SessionState.Listening or SessionState.Degraded or SessionState.Paused)) return false;
            metadata.Validate();
            if (metadata.CapturedAtMs > Now || png.Length is <= 0 or > 134_217_728) throw new ArgumentException("Invalid snapshot time or payload.");
            ClearSnapshotCore();
            store.Apply(new SnapshotAdded
            {
                SchemaVersion = 1,
                EventId = Guid.NewGuid(),
                SessionId = sessionId.Value,
                ReceivedUtc = clock.UtcOrigin.AddMilliseconds(Now),
                Snapshot = metadata
            });
            snapshot = new(sessionId.Value, clock, metadata, png.ToArray(), store.GetContext(metadata.CapturedAtMs));
            revision++;
            return true;
        }
    }
    public SnapshotHandoff? LatestSnapshot { get { lock (gate) return snapshot; } }
    public void ClearSnapshot() { lock (gate) { ClearSnapshotCore(); revision++; } }
    private void ClearSnapshotCore() { if (snapshot is not null) Array.Clear(snapshot.Png); snapshot = null; }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        disposed = true;
    }
}
