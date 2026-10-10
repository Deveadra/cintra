using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MeetingCompanion.Contracts;

namespace MeetingCompanion.Stt;

public sealed record SttOptions
{
    public Uri Endpoint { get; init; } = new("wss://api.openai.com/v1/realtime?intent=transcription");
    public VadOptions Vad { get; init; } = new();
    public int MaxQueuedAudioMs { get; init; } = 2000;
    public int MaxReconnectAttempts { get; init; } = 3;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ConfigurationTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan BackoffBase { get; init; } = TimeSpan.FromMilliseconds(250);

    public void Validate()
    {
        Vad.Validate();
        if (Endpoint.Scheme != "wss" || Endpoint.Host != "api.openai.com" ||
            Endpoint.AbsolutePath != "/v1/realtime" || Endpoint.Query != "?intent=transcription" ||
            MaxQueuedAudioMs is < 100 or > 2000 ||
            MaxReconnectAttempts is < 0 or > 10 || ConnectTimeout <= TimeSpan.Zero ||
            ConfigurationTimeout <= TimeSpan.Zero || BackoffBase <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(SttOptions), "Invalid transcription limits.");
    }
}

public sealed class RealtimeTranscriptionSession : ITranscriptionSession
{
    private const int MaxProviderMessageBytes = 65_536;
    private readonly object gate = new();
    private readonly Queue<AudioFrame> queuedAudio = new();
    private readonly Queue<VadTurn> pendingCommits = new();
    private readonly Dictionary<string, ProviderTurn> providerTurns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<JsonElement>> earlyEvents = new(StringComparer.Ordinal);
    private readonly HashSet<string> seenEventIds = new(StringComparer.Ordinal);
    private readonly Queue<string> seenOrder = new();
    private readonly HashSet<string> finalizedItemIds = new(StringComparer.Ordinal);
    private readonly Queue<string> finalizedOrder = new();
    private readonly SemaphoreSlim queuedSignal = new(0);
    private readonly Channel<ConversationEvent> events = Channel.CreateBounded<ConversationEvent>(
        new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = false });
    private readonly IRealtimeSocketFactory sockets;
    private readonly Func<string?> apiKeyProvider;
    private readonly SttOptions options;
    private readonly LocalVad vad;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource firstReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? worker;
    private CaptureGap? pendingGap;
    private ManualCommit? manualCommit;
    private int queuedMs;
    private long lastCapturedEndMs;
    private bool started;
    private bool stopped;
    private bool disposed;
    private bool terminal;
    private bool acceptingAudio;

    public RealtimeTranscriptionSession(Guid sessionId, AudioStreamId streamId,
        IRealtimeSocketFactory sockets, Func<string?> apiKeyProvider, SttOptions? options = null)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Session ID required.", nameof(sessionId));
        if (streamId is not (AudioStreamId.LocalMic or AudioStreamId.RemoteApp))
            throw new ArgumentOutOfRangeException(nameof(streamId));
        SessionId = sessionId;
        StreamId = streamId;
        this.sockets = sockets ?? throw new ArgumentNullException(nameof(sockets));
        this.apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
        this.options = options ?? new SttOptions();
        this.options.Validate();
        vad = new LocalVad(this.options.Vad);
    }

    public Guid SessionId { get; }
    public AudioStreamId StreamId { get; }
    public Guid TransportSessionId { get; } = Guid.NewGuid();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started) throw new InvalidOperationException("Transcription sessions are single-use.");
            started = true;
        }
        worker = Task.Run(RunAsync);
        try { await firstReady.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask AppendAsync(AudioFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        frame.Validate();
        if (frame.StreamId != StreamId) throw new ContractException("Audio stream identity mismatch.");
        lock (gate)
        {
            if (!started || stopped || disposed || terminal) throw new InvalidOperationException("Transcription session is not active.");
            lastCapturedEndMs = Math.Max(lastCapturedEndMs, frame.CapturedEndMs);
            if (!acceptingAudio)
            {
                MergeGap(new CaptureGap(StreamId, frame.CapturedStartMs, frame.CapturedEndMs,
                    GapReason.NetworkLost, frame.Pcm.Length / 2));
                queuedSignal.Release();
                return ValueTask.CompletedTask;
            }
            queuedAudio.Enqueue(frame);
            queuedMs += checked((int)(frame.CapturedEndMs - frame.CapturedStartMs));
            long? droppedStart = null;
            long droppedEnd = 0;
            long droppedSamples = 0;
            while (queuedMs > options.MaxQueuedAudioMs && queuedAudio.Count > 0)
            {
                var dropped = queuedAudio.Dequeue();
                droppedStart ??= dropped.CapturedStartMs;
                droppedEnd = dropped.CapturedEndMs;
                droppedSamples += dropped.Pcm.Length / 2;
                queuedMs -= checked((int)(dropped.CapturedEndMs - dropped.CapturedStartMs));
            }
            if (droppedStart is not null)
                MergeGap(new CaptureGap(StreamId, droppedStart.Value, droppedEnd, GapReason.QueueOverflow, droppedSamples));
        }
        queuedSignal.Release();
        return ValueTask.CompletedTask;
    }

    /// <summary>Capture loss invalidates the uncommitted provider buffer; committed turns remain eligible for finals.</summary>
    public void MarkCaptureGap(CaptureGap gap)
    {
        gap.Validate();
        if (gap.StreamId != StreamId) throw new ContractException("Capture gap stream identity mismatch.");
        lock (gate)
        {
            if (stopped) return;
            if (queuedAudio.Count > 0)
            {
                var first = queuedAudio.Peek();
                var last = queuedAudio.Last();
                gap = new CaptureGap(StreamId,
                    Math.Min(gap.CapturedStartMs, first.CapturedStartMs),
                    Math.Max(gap.CapturedEndMs, last.CapturedEndMs), gap.Reason,
                    gap.DroppedFrames + queuedAudio.Sum(x => x.Pcm.Length / 2));
            }
            queuedAudio.Clear();
            queuedMs = 0;
            MergeGap(gap);
            lastCapturedEndMs = Math.Max(lastCapturedEndMs, gap.CapturedEndMs);
        }
        queuedSignal.Release();
    }

    public Task CommitTurnAsync(Guid turnId, long capturedStartMs, long capturedEndMs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (turnId == Guid.Empty || capturedStartMs < 0 || capturedEndMs < capturedStartMs)
            throw new ContractException("Invalid explicit turn bounds.");
        var request = new ManualCommit(new VadTurn(turnId, capturedStartMs, capturedEndMs));
        lock (gate)
        {
            if (!started || stopped || terminal) throw new InvalidOperationException("Transcription session is not active.");
            if (manualCommit is not null) throw new InvalidOperationException("A turn commit is already pending.");
            manualCommit = request;
        }
        queuedSignal.Release();
        return request.Completion.Task.WaitAsync(cancellationToken);
    }

    public IAsyncEnumerable<ConversationEvent> ReadAllAsync(CancellationToken cancellationToken) =>
        events.Reader.ReadAllAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? running;
        lock (gate)
        {
            if (stopped) return;
            stopped = true;
            queuedAudio.Clear();
            queuedMs = 0;
            pendingGap = null;
            acceptingAudio = false;
            manualCommit?.Completion.TrySetCanceled();
            manualCommit = null;
            running = worker;
        }
        lifetime.Cancel();
        queuedSignal.Release();
        if (running is not null)
        {
            try { await running.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { await running.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); throw; }
        }
        events.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        disposed = true;
        lifetime.Dispose();
        queuedSignal.Dispose();
    }

    private void MergeGap(CaptureGap gap)
    {
        pendingGap = pendingGap is null ? gap : new CaptureGap(StreamId,
            Math.Min(pendingGap.CapturedStartMs, gap.CapturedStartMs),
            Math.Max(pendingGap.CapturedEndMs, gap.CapturedEndMs), gap.Reason,
            pendingGap.DroppedFrames + gap.DroppedFrames);
    }

    private async Task RunAsync()
    {
        var attempts = 0;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await PublishHealthAsync(attempts == 0 ? SessionState.Starting : SessionState.Recovering,
                    attempts == 0 ? HealthStatus.Starting : HealthStatus.Reconnecting, null, lifetime.Token).ConfigureAwait(false);
                IRealtimeSocket? socket = null;
                CancellationTokenSource? connection = null;
                Task? receive = null;
                Task? send = null;
                try
                {
                    var key = apiKeyProvider();
                    if (string.IsNullOrWhiteSpace(key)) throw new ProviderFailure("missing_api_key", true);
                    socket = sockets.Create();
                    using (var connect = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                    {
                        connect.CancelAfter(options.ConnectTimeout);
                        await socket.ConnectAsync(options.Endpoint, key, connect.Token).ConfigureAwait(false);
                    }
                    var configured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    await socket.SendTextAsync(SessionUpdate, lifetime.Token).ConfigureAwait(false);
                    connection = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    receive = ReceiveLoopAsync(socket, configured, connection.Token);
                    var first = await Task.WhenAny(configured.Task, receive)
                        .WaitAsync(options.ConfigurationTimeout, connection.Token).ConfigureAwait(false);
                    await first.ConfigureAwait(false);
                    if (first == receive) throw new IOException("Provider closed before configuration.");
                    attempts = 0;
                    lock (gate) acceptingAudio = true;
                    await PublishHealthAsync(SessionState.Ready, HealthStatus.Healthy, null, lifetime.Token).ConfigureAwait(false);
                    firstReady.TrySetResult();
                    send = SendLoopAsync(socket, connection.Token);
                    var finished = await Task.WhenAny(receive, send).ConfigureAwait(false);
                    connection.Cancel();
                    try { await Task.WhenAll(receive, send).ConfigureAwait(false); }
                    catch { await finished.ConfigureAwait(false); throw; }
                    throw new IOException("Transcription transport stopped unexpectedly.");
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    connection?.Cancel();
                    if (receive is not null || send is not null)
                        try { await Task.WhenAll(new[] { receive, send }.OfType<Task>()).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                        catch { /* Failure is classified by the first connection error. */ }
                    var failure = error as ProviderFailure ?? ClassifyConnectFailure(error);
                    var code = failure?.Code ?? (error is TimeoutException or OperationCanceledException ? "provider_timeout" : "provider_disconnected");
                    await AbandonConnectionAsync(code, lifetime.Token).ConfigureAwait(false);
                    attempts++;
                    if (failure?.Fatal == true || attempts > options.MaxReconnectAttempts)
                    {
                        await PublishHealthAsync(SessionState.Error,
                            failure?.Fatal == true ? HealthStatus.PermissionDenied : HealthStatus.Failed, code, lifetime.Token).ConfigureAwait(false);
                        firstReady.TrySetException(new IOException("Transcription unavailable: " + code));
                        break;
                    }
                    var delay = TimeSpan.FromMilliseconds(Math.Min(4000,
                        options.BackoffBase.TotalMilliseconds * (1 << (attempts - 1)) + Random.Shared.Next(0, 100)));
                    await Task.Delay(delay, lifetime.Token).ConfigureAwait(false);
                }
                finally
                {
                    connection?.Cancel();
                    if (receive is not null || send is not null)
                        try { await Task.WhenAll(new[] { receive, send }.OfType<Task>()).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                        catch { /* The connection is closing or has failed. */ }
                    connection?.Dispose();
                    if (socket is not null)
                    {
                        using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        try { await socket.CloseAsync(close.Token).ConfigureAwait(false); } catch { /* Socket already failed. */ }
                        await socket.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            firstReady.TrySetCanceled();
            lock (gate)
            {
                terminal = true;
                acceptingAudio = false;
                manualCommit?.Completion.TrySetCanceled();
                manualCommit = null;
                queuedAudio.Clear();
                queuedMs = 0;
                pendingCommits.Clear();
                providerTurns.Clear();
                earlyEvents.Clear();
                vad.Abandon();
            }
            events.Writer.TryComplete();
        }
    }

    private async Task SendLoopAsync(IRealtimeSocket socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await queuedSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            CaptureGap? gap;
            AudioFrame? frame;
            ManualCommit? manual;
            lock (gate)
            {
                gap = pendingGap;
                pendingGap = null;
                manual = manualCommit;
                manualCommit = null;
                frame = queuedAudio.Count > 0 ? queuedAudio.Dequeue() : null;
                if (frame is not null) queuedMs -= checked((int)(frame.CapturedEndMs - frame.CapturedStartMs));
            }
            if (gap is not null)
            {
                var abandoned = vad.Abandon();
                var start = abandoned is null ? gap.CapturedStartMs : Math.Min(abandoned.StartMs, gap.CapturedStartMs);
                await socket.SendTextAsync("{\"type\":\"input_audio_buffer.clear\"}", cancellationToken).ConfigureAwait(false);
                await PublishGapAsync(new CaptureGap(StreamId, start, Math.Max(gap.CapturedEndMs, vad.LastEndMs),
                    gap.Reason, gap.DroppedFrames), cancellationToken).ConfigureAwait(false);
            }
            if (manual is not null)
            {
                if (!vad.InTurn || manual.Bounds.StartMs > vad.LastEndMs || manual.Bounds.EndMs < vad.TurnStartMs)
                    manual.Completion.TrySetException(new ContractException("Cannot commit an empty or unrelated VAD turn."));
                else
                {
                    vad.Abandon();
                    try
                    {
                        lock (gate)
                        {
                            if (pendingCommits.Count + providerTurns.Count >= 128)
                                throw new IOException("Too many pending transcription turns.");
                            pendingCommits.Enqueue(manual.Bounds);
                        }
                        await socket.SendTextAsync("{\"type\":\"input_audio_buffer.commit\"}", cancellationToken).ConfigureAwait(false);
                        manual.Completion.TrySetResult();
                    }
                    catch (Exception error) { manual.Completion.TrySetException(error); throw; }
                }
            }
            if (frame is null) continue;
            if (lastCapturedEndMs - frame.CapturedEndMs > options.MaxQueuedAudioMs)
            {
                MarkCaptureGap(new CaptureGap(StreamId, frame.CapturedStartMs, frame.CapturedEndMs,
                    GapReason.QueueOverflow, frame.Pcm.Length / 2));
                continue;
            }
            var result = vad.Process(frame);
            foreach (var audio in result.Frames)
                await socket.SendTextAsync(JsonSerializer.Serialize(new { type = "input_audio_buffer.append", audio = Convert.ToBase64String(audio.Pcm) }),
                    cancellationToken).ConfigureAwait(false);
            if (result.EndedTurn is not null)
            {
                lock (gate)
                {
                    if (pendingCommits.Count + providerTurns.Count >= 128)
                        throw new IOException("Too many pending transcription turns.");
                    pendingCommits.Enqueue(result.EndedTurn);
                }
                await socket.SendTextAsync("{\"type\":\"input_audio_buffer.commit\"}", cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ReceiveLoopAsync(IRealtimeSocket socket, TaskCompletionSource configured, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read.MessageType == WebSocketMessageType.Close) throw new IOException("Provider closed WebSocket.");
            if (read.MessageType != WebSocketMessageType.Text) throw new IOException("Unexpected provider frame type.");
            if (message.Length + read.Count > MaxProviderMessageBytes) throw new IOException("Provider message exceeds limit.");
            message.Write(buffer, 0, read.Count);
            if (!read.EndOfMessage) continue;
            message.Position = 0;
            using var json = await JsonDocument.ParseAsync(message, cancellationToken: cancellationToken).ConfigureAwait(false);
            message.SetLength(0);
            await HandleProviderEventAsync(json.RootElement, configured, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleProviderEventAsync(JsonElement root, TaskCompletionSource configured, CancellationToken cancellationToken)
    {
        var type = Text(root, "type");
        if (type == "session.updated") { configured.TrySetResult(); return; }
        if (type == "error")
        {
            var code = root.TryGetProperty("error", out var error) ? Text(error, "code") : null;
            throw new ProviderFailure(code ?? "provider_error", code is "invalid_api_key" or "authentication_error" or "permission_denied");
        }
        if (type is "session.expired" or "session.ended") throw new ProviderFailure("session_expired", false);
        if (type == "input_audio_buffer.committed")
        {
            var itemId = Text(root, "item_id");
            if (string.IsNullOrWhiteSpace(itemId)) throw new IOException("Missing committed item ID.");
            List<JsonElement>? early = null;
            lock (gate)
            {
                if (providerTurns.ContainsKey(itemId) || finalizedItemIds.Contains(itemId)) return;
                if (pendingCommits.Count == 0) throw new IOException("Unexpected provider commit.");
                if (providerTurns.Count >= 128) throw new IOException("Too many provider items.");
                providerTurns.Add(itemId, new ProviderTurn(pendingCommits.Dequeue()));
                if (earlyEvents.Remove(itemId, out var queued)) early = queued;
            }
            if (early is not null)
                foreach (var saved in early) await HandleProviderEventAsync(saved, configured, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (type is not ("conversation.item.input_audio_transcription.delta" or
            "conversation.item.input_audio_transcription.completed" or
            "conversation.item.input_audio_transcription.failed")) return;
        var providerItemId = Text(root, "item_id");
        if (string.IsNullOrWhiteSpace(providerItemId)) throw new IOException("Missing provider item ID.");
        ProviderTurn? turn;
        lock (gate)
        {
            if (finalizedItemIds.Contains(providerItemId)) return;
            if (!providerTurns.TryGetValue(providerItemId, out turn))
            {
                if (earlyEvents.Count >= 128 && !earlyEvents.ContainsKey(providerItemId))
                    throw new IOException("Too many unmatched provider items.");
                if (!earlyEvents.TryGetValue(providerItemId, out var list))
                    earlyEvents[providerItemId] = list = [];
                if (list.Count >= 64) throw new IOException("Too many unmatched provider events.");
                list.Add(root.Clone());
                return;
            }
            var eventId = Text(root, "event_id");
            if (eventId is not null && !seenEventIds.Add(eventId)) return;
            if (eventId is not null)
            {
                seenOrder.Enqueue(eventId);
                if (seenOrder.Count > 512) seenEventIds.Remove(seenOrder.Dequeue());
            }
            if (turn.Final) return;
        }
        if (type.EndsWith(".failed", StringComparison.Ordinal))
        {
            lock (gate) FinalizeTurn(providerItemId, turn);
            await PublishGapAsync(new CaptureGap(StreamId, turn.Bounds.StartMs, turn.Bounds.EndMs, GapReason.Unknown, 0),
                cancellationToken).ConfigureAwait(false);
            await PublishHealthAsync(SessionState.Degraded, HealthStatus.Failed, "transcription_failed", cancellationToken).ConfigureAwait(false);
            return;
        }
        var piece = Text(root, type.EndsWith(".delta", StringComparison.Ordinal) ? "delta" : "transcript");
        if (string.IsNullOrWhiteSpace(piece)) return;
        TranscriptEvent? output;
        lock (gate)
        {
            if (turn.Final) return;
            if (type.EndsWith(".completed", StringComparison.Ordinal))
            {
                FinalizeTurn(providerItemId, turn);
                output = MakeTranscript(turn.Bounds, providerItemId, piece, true);
            }
            else
            {
                turn.Text.Append(piece);
                output = MakeTranscript(turn.Bounds, providerItemId, turn.Text.ToString(), false);
            }
        }
        output.Validate();
        await events.Writer.WriteAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private TranscriptEvent MakeTranscript(VadTurn turn, string providerItemId, string text, bool final)
    {
        var attribution = StreamId == AudioStreamId.LocalMic ? Attribution.You : Attribution.Remote;
        var confidence = StreamId == AudioStreamId.LocalMic ? AttributionConfidence.Inferred : AttributionConfidence.SourceConfirmed;
        var note = StreamId == AudioStreamId.LocalMic ? "Microphone may contain acoustic echo." : null;
        if (final)
            return new TranscriptFinal
            {
                SchemaVersion = ContractVersion.Current,
                EventId = Guid.NewGuid(),
                SessionId = SessionId,
                ReceivedUtc = DateTimeOffset.UtcNow,
                StreamId = StreamId,
                TurnId = turn.TurnId,
                ProviderItemId = providerItemId,
                CapturedStartMs = turn.StartMs,
                CapturedEndMs = turn.EndMs,
                Text = text,
                Attribution = attribution,
                AttributionConfidence = confidence,
                ConfidenceNote = note
            };
        return new TranscriptPartial
        {
            SchemaVersion = ContractVersion.Current,
            EventId = Guid.NewGuid(),
            SessionId = SessionId,
            ReceivedUtc = DateTimeOffset.UtcNow,
            StreamId = StreamId,
            TurnId = turn.TurnId,
            ProviderItemId = providerItemId,
            CapturedStartMs = turn.StartMs,
            CapturedEndMs = turn.EndMs,
            Text = text,
            Attribution = attribution,
            AttributionConfidence = confidence,
            ConfidenceNote = note
        };
    }

    private async Task AbandonConnectionAsync(string code, CancellationToken cancellationToken)
    {
        long? start = null;
        long end;
        lock (gate)
        {
            acceptingAudio = false;
            var active = vad.Abandon();
            if (active is not null) start = active.StartMs;
            if (pendingCommits.Count > 0) start = Math.Min(start ?? long.MaxValue, pendingCommits.Min(x => x.StartMs));
            if (providerTurns.Count > 0) start = Math.Min(start ?? long.MaxValue, providerTurns.Values.Min(x => x.Bounds.StartMs));
            if (queuedAudio.Count > 0) start = Math.Min(start ?? long.MaxValue, queuedAudio.Peek().CapturedStartMs);
            if (pendingGap is not null) start = Math.Min(start ?? long.MaxValue, pendingGap.CapturedStartMs);
            end = lastCapturedEndMs;
            queuedAudio.Clear();
            queuedMs = 0;
            pendingGap = null;
            manualCommit?.Completion.TrySetException(new IOException("Turn interrupted by connection loss."));
            manualCommit = null;
            pendingCommits.Clear();
            providerTurns.Clear();
            earlyEvents.Clear();
            seenEventIds.Clear();
            seenOrder.Clear();
            finalizedItemIds.Clear();
            finalizedOrder.Clear();
        }
        if (start is not null)
            await PublishGapAsync(new CaptureGap(StreamId, start.Value, Math.Max(start.Value, end), GapReason.NetworkLost, 0),
                cancellationToken).ConfigureAwait(false);
        await PublishHealthAsync(SessionState.Degraded, HealthStatus.Disconnected, code, cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishGapAsync(CaptureGap gap, CancellationToken cancellationToken)
    {
        var output = new AudioGapEvent
        {
            SchemaVersion = ContractVersion.Current,
            EventId = Guid.NewGuid(),
            SessionId = SessionId,
            ReceivedUtc = DateTimeOffset.UtcNow,
            Gap = gap
        };
        output.Validate();
        await events.Writer.WriteAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishHealthAsync(SessionState state, HealthStatus status, string? code, CancellationToken cancellationToken)
    {
        var output = new SessionStateEvent
        {
            SchemaVersion = ContractVersion.Current,
            EventId = Guid.NewGuid(),
            SessionId = SessionId,
            ReceivedUtc = DateTimeOffset.UtcNow,
            State = state,
            Health = [new ComponentHealth(StreamId == AudioStreamId.LocalMic ? Component.LocalStt : Component.RemoteStt,
                status, Math.Max(0, lastCapturedEndMs), code)]
        };
        output.Validate();
        await events.Writer.WriteAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private const string SessionUpdate = "{\"type\":\"session.update\",\"session\":{\"type\":\"transcription\",\"audio\":{\"input\":{\"format\":{\"type\":\"audio/pcm\",\"rate\":24000},\"transcription\":{\"model\":\"gpt-live-transcribe\"},\"turn_detection\":null}}}}";

    private void FinalizeTurn(string itemId, ProviderTurn turn)
    {
        turn.Final = true;
        providerTurns.Remove(itemId);
        finalizedItemIds.Add(itemId);
        finalizedOrder.Enqueue(itemId);
        if (finalizedOrder.Count > 512) finalizedItemIds.Remove(finalizedOrder.Dequeue());
    }

    private static ProviderFailure? ClassifyConnectFailure(Exception error)
    {
        var http = error as HttpRequestException ?? error.InnerException as HttpRequestException;
        return http?.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ProviderFailure("invalid_credentials", true),
            HttpStatusCode.TooManyRequests => new ProviderFailure("rate_limited", false),
            _ => null
        };
    }

    private sealed class ProviderTurn(VadTurn bounds)
    {
        public VadTurn Bounds { get; } = bounds;
        public StringBuilder Text { get; } = new();
        public bool Final { get; set; }
    }

    private sealed class ProviderFailure(string code, bool fatal) : Exception
    {
        public string Code { get; } = code.Length <= 128 ? code : "provider_error";
        public bool Fatal { get; } = fatal;
    }

    private sealed class ManualCommit(VadTurn bounds)
    {
        public VadTurn Bounds { get; } = bounds;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
