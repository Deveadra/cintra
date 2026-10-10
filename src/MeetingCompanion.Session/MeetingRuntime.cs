using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MeetingCompanion.Audio;
using MeetingCompanion.Contracts;
using MeetingCompanion.Stt;

namespace MeetingCompanion.Session;

/// <summary>Live sockets require an operator gate. Offline mode never reads a key or opens a network socket.</summary>
public sealed class MeetingRuntimeFactory(string helperPath, Func<bool> paidProviderApproved,
    Func<string?> keyProvider, Func<IAudioCaptureSession>? captureFactory = null,
    Func<AudioStreamId, IRealtimeSocketFactory>? testSockets = null, SttOptions? sttOptions = null) : IMeetingRuntimeFactory
{
    public IMeetingRuntime Create(Guid sessionId, ProviderMode mode, Action<CaptureMessage> observe)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        string? key = null;
        if (mode == ProviderMode.OpenAi)
        {
            if (!paidProviderApproved()) throw new InvalidOperationException("Explicit paid-provider approval is required.");
            key = keyProvider();
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Transcription unavailable — configure key.");
        }
        if (captureFactory is null && !File.Exists(helperPath)) throw new FileNotFoundException("Build the native audio helper before Start.", helperPath);
        var capture = new ObservedCapture(sessionId, captureFactory?.Invoke() ?? new NativeAudioCaptureSession(helperPath), observe);
        if (mode == ProviderMode.Unavailable) return new CaptureOnlyRuntime(capture);
        try
        {
            var local = new ObservedSocketFactory(mode == ProviderMode.OfflineTest
                ? testSockets?.Invoke(AudioStreamId.LocalMic) ?? new ScriptedSocketFactory("TEST MODE — scripted YOU turn; not recognized speech.")
                : new OpenAiRealtimeSocketFactory(), Component.LocalStt);
            var remote = new ObservedSocketFactory(mode == ProviderMode.OfflineTest
                ? testSockets?.Invoke(AudioStreamId.RemoteApp) ?? new ScriptedSocketFactory("TEST MODE — scripted REMOTE turn; not recognized speech.")
                : new OpenAiRealtimeSocketFactory(), Component.RemoteStt);
            return new TranscriptionRuntime(new DualStreamTranscriber(capture,
            stream => stream == AudioStreamId.LocalMic ? local : remote, () => mode == ProviderMode.OfflineTest ? "offline-only" : key, sttOptions), local, remote);
        }
        catch { capture.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
    }
}

internal sealed class ObservedCapture(Guid meetingId, IAudioCaptureSession inner, Action<CaptureMessage> observe) : IAudioCaptureSession
{
    // Native envelopes retain the native session's ID; the transcriber/store use the stable meeting ID.
    public Guid SessionId => meetingId;
    public Task StartAsync(CaptureSelection selection, ClockMapping clock, CancellationToken token) => inner.StartAsync(selection, clock, token);
    public async IAsyncEnumerable<CaptureMessage> ReadAllAsync([EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var item in inner.ReadAllAsync(token).ConfigureAwait(false)) { observe(item); yield return item; }
    }
    public Task PauseAsync(CancellationToken token) => inner.PauseAsync(token);
    public Task ResumeAsync(CancellationToken token) => inner.ResumeAsync(token);
    public Task StopAsync(CancellationToken token) => inner.StopAsync(token);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
internal sealed class TranscriptionRuntime(DualStreamTranscriber transcriber, ObservedSocketFactory local,
    ObservedSocketFactory remote) : IMeetingRuntime
{
    public Task RunAsync(CaptureSelection selection, ClockMapping clock, CancellationToken token) => transcriber.RunAsync(selection, clock, token);
    public IAsyncEnumerable<ConversationEvent> ReadAllAsync(CancellationToken token) => transcriber.ReadAllAsync(token);
    public ComponentHealth[] SampleTransportHealth(long nowMs) => [local.Sample(nowMs), remote.Sample(nowMs)];
    public ValueTask DisposeAsync() => transcriber.DisposeAsync();
}
internal sealed class CaptureOnlyRuntime(ObservedCapture capture) : IMeetingRuntime
{
    private readonly Channel<ConversationEvent> events = Channel.CreateBounded<ConversationEvent>(128);
    public async Task RunAsync(CaptureSelection selection, ClockMapping clock, CancellationToken token)
    {
        try
        {
            await capture.StartAsync(selection, clock, token).ConfigureAwait(false);
            await foreach (var message in capture.ReadAllAsync(token).ConfigureAwait(false))
            {
                ConversationEvent? item = message switch
                {
                    CaptureGap gap => new AudioGapEvent { SchemaVersion = 1, EventId = Guid.NewGuid(), SessionId = capture.SessionId, ReceivedUtc = DateTimeOffset.UtcNow, Gap = gap },
                    CaptureSourceChanged change => new SourceChangedEvent { SchemaVersion = 1, EventId = Guid.NewGuid(), SessionId = capture.SessionId, ReceivedUtc = DateTimeOffset.UtcNow, Change = change },
                    _ => null
                };
                if (item is not null) await events.Writer.WriteAsync(item, token).ConfigureAwait(false);
            }
        }
        finally { try { await capture.StopAsync(CancellationToken.None).ConfigureAwait(false); } finally { events.Writer.TryComplete(); } }
    }
    public IAsyncEnumerable<ConversationEvent> ReadAllAsync(CancellationToken token) => events.Reader.ReadAllAsync(token);
    public ComponentHealth[] SampleTransportHealth(long nowMs) => [];
    public ValueTask DisposeAsync() => capture.DisposeAsync();
}

/// <summary>Samples configured transport lifetime, never transcription accuracy. Receive/send failure closes health immediately.</summary>
internal sealed class ObservedSocketFactory(IRealtimeSocketFactory inner, Component component) : IRealtimeSocketFactory
{
    private volatile ObservedSocket? latest;
    public IRealtimeSocket Create() => latest = new(inner.Create());
    public ComponentHealth Sample(long nowMs)
    {
        bool configured = latest?.Configured == true;
        return new(component, configured ? HealthStatus.Healthy : HealthStatus.Reconnecting, nowMs,
            configured ? "configured_transport_open" : "transport_not_configured");
    }
    private sealed class ObservedSocket(IRealtimeSocket inner) : IRealtimeSocket
    {
        private volatile bool configured;
        private readonly MemoryStream message = new();
        public bool Configured => configured;
        public Task ConnectAsync(Uri endpoint, string key, CancellationToken token) => inner.ConnectAsync(endpoint, key, token);
        public async Task SendTextAsync(string json, CancellationToken token)
        {
            try { await inner.SendTextAsync(json, token).ConfigureAwait(false); }
            catch { configured = false; throw; }
        }
        public async Task<SocketRead> ReceiveAsync(Memory<byte> buffer, CancellationToken token)
        {
            try
            {
                var read = await inner.ReceiveAsync(buffer, token).ConfigureAwait(false);
                if (read.MessageType == WebSocketMessageType.Close) configured = false;
                if (message.Length + read.Count <= 65_536) message.Write(buffer.Span[..read.Count]);
                if (read.EndOfMessage)
                {
                    using var document = JsonDocument.Parse(message.ToArray());
                    if (document.RootElement.TryGetProperty("type", out var type) && type.GetString() == "session.updated") configured = true;
                    if (type.GetString() == "error") configured = false;
                    message.SetLength(0);
                }
                return read;
            }
            catch { configured = false; throw; }
        }
        public async Task CloseAsync(CancellationToken token) { configured = false; await inner.CloseAsync(token).ConfigureAwait(false); }
        public async ValueTask DisposeAsync() { configured = false; message.Dispose(); await inner.DisposeAsync().ConfigureAwait(false); }
    }
}

/// <summary>Explicit TEST MODE. Commits yield canned text through the real STT parser/VAD, never a speech claim.</summary>
public sealed class ScriptedSocketFactory(string transcript) : IRealtimeSocketFactory
{
    public IRealtimeSocket Create() => new ScriptedSocket(transcript);
    private sealed class ScriptedSocket(string transcript) : IRealtimeSocket
    {
        private readonly Channel<byte[]> replies = Channel.CreateBounded<byte[]>(64);
        private byte[]? pending;
        private int offset;
        private int turn;
        public Task ConnectAsync(Uri endpoint, string key, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public async Task SendTextAsync(string json, CancellationToken token)
        {
            using var document = JsonDocument.Parse(json);
            var type = document.RootElement.GetProperty("type").GetString();
            if (type == "session.update") await Emit(new { type = "session.updated" }, token).ConfigureAwait(false);
            if (type == "input_audio_buffer.commit")
            {
                string item = "offline-" + ++turn;
                await Emit(new { type = "input_audio_buffer.committed", item_id = item }, token).ConfigureAwait(false);
                await Emit(new { type = "conversation.item.input_audio_transcription.delta", item_id = item, event_id = item + "-delta", delta = "TEST MODE — scripted…" }, token).ConfigureAwait(false);
                await Emit(new { type = "conversation.item.input_audio_transcription.completed", item_id = item, event_id = item + "-final", transcript }, token).ConfigureAwait(false);
            }
        }
        private ValueTask Emit(object reply, CancellationToken token) => replies.Writer.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply)), token);
        public async Task<SocketRead> ReceiveAsync(Memory<byte> buffer, CancellationToken token)
        {
            pending ??= await replies.Reader.ReadAsync(token).ConfigureAwait(false);
            int count = Math.Min(buffer.Length, pending.Length - offset);
            pending.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            bool end = offset == pending.Length;
            if (end) { pending = null; offset = 0; }
            return new(count, end, WebSocketMessageType.Text);
        }
        public Task CloseAsync(CancellationToken token) { replies.Writer.TryComplete(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { replies.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
