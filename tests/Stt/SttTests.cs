using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MeetingCompanion.Contracts;
using MeetingCompanion.Stt;

namespace MeetingCompanion.Stt.Tests;

public sealed class SttTests
{
    private static readonly VadOptions FastVad = new(0.01, 40, 100, 40, 1000);
    private static readonly SttOptions FastOptions = new() { Vad = FastVad, BackoffBase = TimeSpan.FromMilliseconds(10) };

    [Fact]
    public void VadIgnoresSilenceAndShortPauseThenCommitsOnce()
    {
        var vad = new LocalVad(new VadOptions(0.01, 40, 100, 40, 1000));
        var turns = new List<VadTurn>();
        var sent = 0;
        var sequence = 0;
        foreach (var amplitude in Enumerable.Repeat((short)0, 5)
            .Concat(Enumerable.Repeat((short)5000, 5))
            .Concat(Enumerable.Repeat((short)0, 3))
            .Concat(Enumerable.Repeat((short)5000, 5))
            .Concat(Enumerable.Repeat((short)0, 6)))
        {
            var result = vad.Process(Frame(AudioStreamId.LocalMic, sequence++, amplitude));
            sent += result.Frames.Count;
            if (result.EndedTurn is not null) turns.Add(result.EndedTurn);
        }
        Assert.Single(turns);
        Assert.True(sent > 10);
        Assert.False(vad.InTurn);
        Assert.Equal(0, turns[0].StartMs % 20);
        Assert.Null(vad.Abandon());
    }

    [Fact]
    public void VadBoundsContinuousSpeechAtMaximumTurnLength()
    {
        var vad = new LocalVad(new VadOptions(0.01, 40, 100, 40, 1000));
        var turns = new List<VadTurn>();
        for (var i = 0; i < 110; i++)
        {
            var result = vad.Process(Frame(AudioStreamId.LocalMic, i, 5000));
            if (result.EndedTurn is not null) turns.Add(result.EndedTurn);
        }
        Assert.Equal(2, turns.Count);
        Assert.All(turns, turn => Assert.InRange(turn.EndMs - turn.StartMs, 1000, 1020));
        Assert.True(vad.InTurn);
    }

    [Fact]
    public async Task FragmentedEarlyDeltaAndOutOfOrderFinalsUseProviderItemIds()
    {
        var socket = new FakeSocket();
        await using var session = NewSession(AudioStreamId.LocalMic, socket);
        await session.StartAsync(CancellationToken.None);
        await FeedTurnAsync(session, 0);
        await socket.WaitSentAsync("input_audio_buffer.commit");
        await FeedTurnAsync(session, 30);
        await socket.WaitSentAsync("input_audio_buffer.commit");
        socket.Emit(new { type = "conversation.item.input_audio_transcription.delta", event_id = "e-early", item_id = "item-1", delta = "Hel" }, 3);
        socket.Emit(new { type = "input_audio_buffer.committed", item_id = "item-1" }, 4);
        socket.Emit(new { type = "input_audio_buffer.committed", item_id = "item-2" }, 2);
        socket.Emit(new { type = "conversation.item.input_audio_transcription.completed", event_id = "e-final-2", item_id = "item-2", transcript = "Second" }, 5);
        socket.Emit(new { type = "conversation.item.input_audio_transcription.delta", event_id = "e-more", item_id = "item-1", delta = "lo" }, 3);
        socket.Emit(new { type = "conversation.item.input_audio_transcription.completed", event_id = "e-final-1", item_id = "item-1", transcript = "Hello" }, 4);
        socket.Emit(new { type = "conversation.item.input_audio_transcription.delta", event_id = "e-stale", item_id = "item-2", delta = "wrong" }, 3);
        socket.Emit(new { type = "conversation.item.input_audio_transcription.completed", event_id = "e-final-2", item_id = "item-2", transcript = "duplicate" }, 3);
        var output = await CollectAsync(session.ReadAllAsync(CancellationToken.None), x => x is TranscriptFinal, 2);
        var finals = output.Cast<TranscriptFinal>().ToArray();
        Assert.Equal(["Second", "Hello"], finals.Select(x => x.Text));
        Assert.Equal(["item-2", "item-1"], finals.Select(x => x.ProviderItemId));
        Assert.True(finals[0].CapturedStartMs > finals[1].CapturedStartMs);
        Assert.All(finals, x => { x.Validate(); Assert.Equal(Attribution.You, x.Attribution); });
    }

    [Fact]
    public async Task IndependentMicAndSyntheticRemoteFramesProduceSeparateFinals()
    {
        var capture = new FakeCapture();
        var micSocket = new FakeSocket { AutoTranscript = "Local rehearsal" };
        var remoteSocket = new FakeSocket { AutoTranscript = "Remote rehearsal" };
        await using var transcriber = new DualStreamTranscriber(capture,
            stream => new FakeSocketFactory(stream == AudioStreamId.LocalMic ? micSocket : remoteSocket),
            () => "offline-fake-key", FastOptions);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var selection = new CaptureSelection(
            new CaptureSource(AudioStreamId.LocalMic, "Fixture mic", "fixture-mic", null, false, false),
            new CaptureSource(AudioStreamId.RemoteApp, "Synthetic renderer", null, Environment.ProcessId, true, false));
        var clock = new ClockMapping(Stopwatch.GetTimestamp(), Stopwatch.Frequency, DateTimeOffset.UtcNow);
        var running = transcriber.RunAsync(selection, clock, cancellation.Token);
        await Task.WhenAll(micSocket.Connected, remoteSocket.Connected);
        for (var i = 0; i < 12; i++)
        {
            capture.Emit(Frame(AudioStreamId.LocalMic, i, i < 4 ? (short)5000 : (short)0));
            capture.Emit(Frame(AudioStreamId.RemoteApp, i, i < 4 ? (short)5000 : (short)0));
        }
        var output = await CollectAsync(transcriber.ReadAllAsync(cancellation.Token), x => x is TranscriptFinal, 2);
        Assert.Equal(2, output.Select(x => ((TranscriptFinal)x).StreamId).Distinct().Count());
        Assert.Contains(output, x => x is TranscriptFinal { StreamId: AudioStreamId.LocalMic, Attribution: Attribution.You });
        Assert.Contains(output, x => x is TranscriptFinal { StreamId: AudioStreamId.RemoteApp, Attribution: Attribution.Remote });
        Assert.NotSame(micSocket, remoteSocket);
        Assert.Equal(1, micSocket.CountSent("input_audio_buffer.commit"));
        Assert.Equal(1, remoteSocket.CountSent("input_audio_buffer.commit"));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task DisconnectMidTurnMarksGapAndFreshConnectionDoesNotReplay()
    {
        var first = new FakeSocket();
        var second = new FakeSocket { AutoTranscript = "Recovered" };
        await using var session = new RealtimeTranscriptionSession(Guid.NewGuid(), AudioStreamId.RemoteApp,
            new FakeSocketFactory(first, second), () => "offline-fake-key", FastOptions);
        await session.StartAsync(CancellationToken.None);
        await session.AppendAsync(Frame(AudioStreamId.RemoteApp, 0, 7000), CancellationToken.None);
        await session.AppendAsync(Frame(AudioStreamId.RemoteApp, 1, 7000), CancellationToken.None);
        await first.WaitSentAsync("input_audio_buffer.append");
        first.EmitClose();
        await second.Connected.WaitAsync(TimeSpan.FromSeconds(3));
        await FeedTurnAsync(session, 20);
        var output = await CollectAsync(session.ReadAllAsync(CancellationToken.None),
            x => x is AudioGapEvent or TranscriptFinal, 2);
        Assert.Contains(output, x => x is AudioGapEvent { Gap.Reason: GapReason.NetworkLost });
        Assert.Contains(output, x => x is TranscriptFinal { Text: "Recovered", StreamId: AudioStreamId.RemoteApp });
        Assert.Equal(2, first.CountSent("input_audio_buffer.append"));
        Assert.DoesNotContain(second.SentAudio, x => x.SequenceEqual(Frame(AudioStreamId.RemoteApp, 0, 7000).Pcm));
    }

    [Fact]
    public async Task FramesCapturedDuringReconnectBecomeGapInsteadOfReplay()
    {
        var first = new FakeSocket();
        var second = new FakeSocket { AutoConfigure = false, AutoTranscript = "Fresh turn" };
        await using var session = new RealtimeTranscriptionSession(Guid.NewGuid(), AudioStreamId.LocalMic,
            new FakeSocketFactory(first, second), () => "offline-fake-key", FastOptions);
        await session.StartAsync(CancellationToken.None);
        first.EmitClose();
        await second.Connected.WaitAsync(TimeSpan.FromSeconds(3));
        await session.AppendAsync(Frame(AudioStreamId.LocalMic, 10, 7000), CancellationToken.None);
        await session.AppendAsync(Frame(AudioStreamId.LocalMic, 11, 7000), CancellationToken.None);
        second.Emit(new { type = "session.updated" });
        await CollectAsync(session.ReadAllAsync(CancellationToken.None),
            x => x is SessionStateEvent { State: SessionState.Ready }, 2);
        await FeedTurnAsync(session, 20);
        var output = await CollectAsync(session.ReadAllAsync(CancellationToken.None),
            x => x is AudioGapEvent or TranscriptFinal, 2);
        Assert.Contains(output, x => x is AudioGapEvent
        {
            Gap.Reason: GapReason.NetworkLost,
            Gap.DroppedFrames: 960
        });
        Assert.Contains(output, x => x is TranscriptFinal { Text: "Fresh turn" });
        Assert.DoesNotContain(second.SentAudio, x => x.SequenceEqual(Frame(AudioStreamId.LocalMic, 10, 7000).Pcm));
    }

    [Fact]
    public async Task QueueOverflowIsBoundedAndReported()
    {
        var socket = new FakeSocket { BlockAudio = true };
        await using var session = NewSession(AudioStreamId.LocalMic, socket,
            new SttOptions { Vad = FastVad, MaxQueuedAudioMs = 200, BackoffBase = TimeSpan.FromMilliseconds(10) });
        await session.StartAsync(CancellationToken.None);
        for (var i = 0; i < 30; i++)
            await session.AppendAsync(Frame(AudioStreamId.LocalMic, i, 5000), CancellationToken.None);
        socket.ReleaseAudio();
        var output = await CollectAsync(session.ReadAllAsync(CancellationToken.None), x => x is AudioGapEvent, 1);
        var gap = Assert.IsType<AudioGapEvent>(output[0]);
        Assert.Equal(GapReason.QueueOverflow, gap.Gap.Reason);
        Assert.True(gap.Gap.DroppedFrames > 0);
        gap.Validate();
    }

    [Fact]
    public async Task InvalidCredentialsAreTerminalAndStopIsIdempotent()
    {
        var socket = new FakeSocket { ConfigureError = "invalid_api_key" };
        await using var session = NewSession(AudioStreamId.LocalMic, socket);
        await Assert.ThrowsAsync<IOException>(() => session.StartAsync(CancellationToken.None));
        await session.StopAsync(CancellationToken.None);
        await session.StopAsync(CancellationToken.None);
        Assert.Equal(1, socket.ConnectCount);
        Assert.True(socket.Closed);
    }

    [Fact]
    public async Task CancelledStartAndWrongStreamRejectWithoutSendingAudio()
    {
        var socket = new FakeSocket();
        await using var session = NewSession(AudioStreamId.LocalMic, socket);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.StartAsync(cancellation.Token));
        await session.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<ContractException>(async () =>
            await session.AppendAsync(Frame(AudioStreamId.RemoteApp, 0, 5000), CancellationToken.None));
        Assert.Empty(socket.SentAudio);
        await session.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExplicitCommitRequiresSpeechAndNeverDuplicatesAutomaticTurn()
    {
        var socket = new FakeSocket { AutoTranscript = "Manual" };
        await using var session = NewSession(AudioStreamId.LocalMic, socket);
        await session.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<ContractException>(() => session.CommitTurnAsync(Guid.NewGuid(), 0, 20, CancellationToken.None));
        await session.AppendAsync(Frame(AudioStreamId.LocalMic, 0, 5000), CancellationToken.None);
        await session.AppendAsync(Frame(AudioStreamId.LocalMic, 1, 5000), CancellationToken.None);
        await socket.WaitSentAsync("input_audio_buffer.append");
        await socket.WaitSentAsync("input_audio_buffer.append");
        await Assert.ThrowsAsync<ContractException>(() => session.CommitTurnAsync(Guid.NewGuid(), 1000, 1040, CancellationToken.None));
        await session.CommitTurnAsync(Guid.NewGuid(), 0, 40, CancellationToken.None);
        var output = await CollectAsync(session.ReadAllAsync(CancellationToken.None), x => x is TranscriptFinal, 1);
        Assert.Single(output);
        Assert.Equal(1, socket.CountSent("input_audio_buffer.commit"));
    }

    [Fact]
    public async Task RateLimitAndExpiredSessionReconnectWithHealthTransition()
    {
        var limited = new FakeSocket { ConfigureError = "rate_limit_exceeded" };
        var healthy = new FakeSocket();
        var replacement = new FakeSocket();
        await using var session = new RealtimeTranscriptionSession(Guid.NewGuid(), AudioStreamId.LocalMic,
            new FakeSocketFactory(limited, healthy, replacement), () => "offline-fake-key", FastOptions);
        await session.StartAsync(CancellationToken.None);
        Assert.True(limited.Closed);
        healthy.Emit(new { type = "session.expired" });
        await replacement.Connected.WaitAsync(TimeSpan.FromSeconds(3));
        var output = await CollectAsync(session.ReadAllAsync(CancellationToken.None),
            x => x is SessionStateEvent { State: SessionState.Degraded }, 2);
        Assert.Contains(output, x => x is SessionStateEvent state &&
            state.Health.Any(h => h.DiagnosticCode == "rate_limit_exceeded"));
        Assert.Contains(output, x => x is SessionStateEvent state &&
            state.Health.Any(h => h.DiagnosticCode == "session_expired"));
    }

    [Fact]
    public async Task ConfigurationTimeoutIsBoundedAnd401HandshakeIsTerminal()
    {
        var timeoutOptions = FastOptions with { ConfigurationTimeout = TimeSpan.FromMilliseconds(30), MaxReconnectAttempts = 1 };
        var a = new FakeSocket { AutoConfigure = false };
        var b = new FakeSocket { AutoConfigure = false };
        await using (var session = new RealtimeTranscriptionSession(Guid.NewGuid(), AudioStreamId.LocalMic,
            new FakeSocketFactory(a, b), () => "offline-fake-key", timeoutOptions))
            await Assert.ThrowsAsync<IOException>(() => session.StartAsync(CancellationToken.None));
        Assert.True(a.Closed && b.Closed);
        var denied = new FakeSocket { ConnectStatus = HttpStatusCode.Unauthorized };
        await using (var session = NewSession(AudioStreamId.LocalMic, denied))
            await Assert.ThrowsAsync<IOException>(() => session.StartAsync(CancellationToken.None));
        Assert.Equal(1, denied.ConnectCount);
    }

    [Fact]
    public async Task CaptureGapClearsUncommittedAudioAndStopCancelsBlockedSend()
    {
        var socket = new FakeSocket { BlockAudio = true };
        await using var session = NewSession(AudioStreamId.LocalMic, socket);
        await session.StartAsync(CancellationToken.None);
        await session.AppendAsync(Frame(AudioStreamId.LocalMic, 0, 5000), CancellationToken.None);
        await session.AppendAsync(Frame(AudioStreamId.LocalMic, 1, 5000), CancellationToken.None);
        session.MarkCaptureGap(new CaptureGap(AudioStreamId.LocalMic, 40, 80, GapReason.DeviceLost, 960));
        socket.ReleaseAudio();
        var gaps = await CollectAsync(session.ReadAllAsync(CancellationToken.None), x => x is AudioGapEvent, 1);
        var gap = Assert.IsType<AudioGapEvent>(gaps[0]).Gap;
        Assert.Equal(GapReason.DeviceLost, gap.Reason);
        Assert.True(gap.CapturedStartMs <= 40);
        Assert.Equal(0, socket.CountSent("input_audio_buffer.commit"));
        await session.StopAsync(CancellationToken.None);
        Assert.True(socket.Closed);
    }

    [Fact]
    public async Task AggregateListeningRequiresBothCaptureAndSttAndSourceLossDegrades()
    {
        var capture = new FakeCapture();
        var mic = new FakeSocket();
        var remote = new FakeSocket();
        var transcriber = new DualStreamTranscriber(capture,
            stream => new FakeSocketFactory(stream == AudioStreamId.LocalMic ? mic : remote),
            () => "offline-fake-key", FastOptions);
        var selection = new CaptureSelection(
            new CaptureSource(AudioStreamId.LocalMic, "Mic", "fixture-mic", null, false, false),
            new CaptureSource(AudioStreamId.RemoteApp, "Renderer", null, Environment.ProcessId, true, false));
        var clock = new ClockMapping(Stopwatch.GetTimestamp(), Stopwatch.Frequency, DateTimeOffset.UtcNow);
        var run = transcriber.RunAsync(selection, clock, CancellationToken.None);
        await Task.WhenAll(mic.Connected, remote.Connected);
        capture.Emit(new CaptureHealth(AudioStreamId.LocalMic,
            new ComponentHealth(Component.LocalCapture, HealthStatus.Healthy, 0, null), 0.1, 0, 0));
        capture.Emit(new CaptureHealth(AudioStreamId.RemoteApp,
            new ComponentHealth(Component.RemoteCapture, HealthStatus.Healthy, 0, null), 0.1, 0, 0));
        var listening = await CollectAsync(transcriber.ReadAllAsync(CancellationToken.None),
            x => x is SessionStateEvent { State: SessionState.Listening }, 1);
        Assert.Single(listening);
        capture.Emit(new CaptureSourceChanged(selection.Incoming, 300));
        var degraded = await CollectAsync(transcriber.ReadAllAsync(CancellationToken.None),
            x => x is SessionStateEvent { State: SessionState.Degraded }, 1);
        Assert.Single(degraded);
        Assert.Contains(((SessionStateEvent)degraded[0]).Health,
            x => x.Component == Component.RemoteCapture && x.Status == HealthStatus.Disconnected);
        await transcriber.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(mic.Closed && remote.Closed);
    }

    [Fact]
    public async Task DuplicateEarlyDeltaIsIgnoredAndProviderFailureMarksOnlyThatTurn()
    {
        var socket = new FakeSocket();
        await using var session = NewSession(AudioStreamId.RemoteApp, socket);
        await session.StartAsync(CancellationToken.None);
        await FeedTurnAsync(session, 0);
        await socket.WaitSentAsync("input_audio_buffer.commit");
        var duplicate = new
        {
            type = "conversation.item.input_audio_transcription.delta",
            event_id = "same-delta",
            item_id = "item-a",
            delta = "Remote"
        };
        socket.Emit(duplicate, 3);
        socket.Emit(duplicate, 3);
        socket.Emit(new { type = "input_audio_buffer.committed", item_id = "item-a" });
        socket.Emit(new { type = "conversation.item.input_audio_transcription.failed", event_id = "failed-a", item_id = "item-a" });
        var output = await CollectAsync(session.ReadAllAsync(CancellationToken.None),
            x => x is TranscriptPartial or AudioGapEvent, 2);
        Assert.Equal("Remote", Assert.IsType<TranscriptPartial>(output[0]).Text);
        Assert.Equal(GapReason.Unknown, Assert.IsType<AudioGapEvent>(output[1]).Gap.Reason);
        Assert.Equal(Attribution.Remote, ((TranscriptPartial)output[0]).Attribution);
    }

    [Fact]
    public async Task CancelledConfigurationClosesSocketAndRepeatedFreshSessionsReleaseIt()
    {
        var pending = new FakeSocket { AutoConfigure = false };
        await using (var session = NewSession(AudioStreamId.LocalMic, pending))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.StartAsync(cancellation.Token));
            Assert.True(pending.Closed);
        }
        for (var i = 0; i < 10; i++)
        {
            var socket = new FakeSocket();
            await using var session = NewSession(AudioStreamId.LocalMic, socket);
            await session.StartAsync(CancellationToken.None);
            await session.StopAsync(CancellationToken.None);
            Assert.True(socket.Closed);
        }
    }

    private static RealtimeTranscriptionSession NewSession(AudioStreamId stream, FakeSocket socket, SttOptions? options = null) =>
        new(Guid.NewGuid(), stream, new FakeSocketFactory(socket), () => "offline-fake-key", options ?? FastOptions);

    private static AudioFrame Frame(AudioStreamId stream, int sequence, short sample)
    {
        var pcm = new byte[960];
        for (var i = 0; i < pcm.Length; i += 2) { pcm[i] = (byte)sample; pcm[i + 1] = (byte)(sample >> 8); }
        return new AudioFrame(stream, sequence, sequence * 20L, sequence * 20L + 20, AudioFormat.Normalized, 48_000, pcm);
    }

    private static async Task FeedTurnAsync(RealtimeTranscriptionSession session, int firstSequence)
    {
        for (var i = 0; i < 12; i++)
            await session.AppendAsync(Frame(session.StreamId, firstSequence + i, i < 4 ? (short)5000 : (short)0), CancellationToken.None);
    }

    private static async Task<List<ConversationEvent>> CollectAsync(IAsyncEnumerable<ConversationEvent> source,
        Func<ConversationEvent, bool> accept, int count)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var collected = new List<ConversationEvent>();
        await foreach (var item in source.WithCancellation(deadline.Token))
        {
            if (!accept(item)) continue;
            collected.Add(item);
            if (collected.Count >= count) break;
        }
        return collected;
    }

    private sealed class FakeSocketFactory(params FakeSocket[] sockets) : IRealtimeSocketFactory
    {
        private readonly Queue<FakeSocket> available = new(sockets);
        public IRealtimeSocket Create() => available.Dequeue();
    }

    private sealed class FakeSocket : IRealtimeSocket
    {
        private readonly Channel<(byte[] Data, bool End, WebSocketMessageType Type)> incoming = Channel.CreateUnbounded<(byte[], bool, WebSocketMessageType)>();
        private readonly Channel<string> sent = Channel.CreateUnbounded<string>();
        private readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource audioGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> sentHistory = new();
        private int itemNumber;
        public string? AutoTranscript { get; init; }
        public string? ConfigureError { get; init; }
        public bool AutoConfigure { get; init; } = true;
        public HttpStatusCode? ConnectStatus { get; init; }
        public bool BlockAudio { get; init; }
        public int ConnectCount { get; private set; }
        public bool Closed { get; private set; }
        public Task Connected => connected.Task;
        public List<byte[]> SentAudio { get; } = [];

        public Task ConnectAsync(Uri endpoint, string apiKey, CancellationToken cancellationToken)
        {
            Assert.Equal("wss", endpoint.Scheme);
            Assert.False(string.IsNullOrWhiteSpace(apiKey));
            ConnectCount++;
            if (ConnectStatus is not null) throw new HttpRequestException("Handshake rejected.", null, ConnectStatus);
            connected.TrySetResult();
            return Task.CompletedTask;
        }

        public async Task SendTextAsync(string json, CancellationToken cancellationToken)
        {
            using var payload = JsonDocument.Parse(json);
            var type = payload.RootElement.GetProperty("type").GetString();
            if (type == "input_audio_buffer.append" && BlockAudio)
                await audioGate.Task.WaitAsync(cancellationToken);
            sentHistory.Enqueue(json);
            await sent.Writer.WriteAsync(json, cancellationToken);
            if (type == "input_audio_buffer.append")
                SentAudio.Add(Convert.FromBase64String(payload.RootElement.GetProperty("audio").GetString()!));
            if (type == "session.update")
            {
                Assert.Equal("transcription", payload.RootElement.GetProperty("session").GetProperty("type").GetString());
                Assert.Equal("gpt-live-transcribe", payload.RootElement.GetProperty("session").GetProperty("audio").GetProperty("input").GetProperty("transcription").GetProperty("model").GetString());
                Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("session").GetProperty("audio").GetProperty("input").GetProperty("turn_detection").ValueKind);
                if (ConfigureError is not null) Emit(new { type = "error", error = new { code = ConfigureError } });
                else if (AutoConfigure) Emit(new { type = "session.updated" });
            }
            if (type == "input_audio_buffer.commit" && AutoTranscript is not null)
            {
                var item = "item-" + Interlocked.Increment(ref itemNumber);
                Emit(new { type = "input_audio_buffer.committed", item_id = item });
                Emit(new { type = "conversation.item.input_audio_transcription.delta", item_id = item, event_id = item + "-delta", delta = AutoTranscript[..Math.Min(4, AutoTranscript.Length)] });
                Emit(new { type = "conversation.item.input_audio_transcription.completed", item_id = item, event_id = item + "-final", transcript = AutoTranscript });
            }
        }

        public async Task<SocketRead> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var packet = await incoming.Reader.ReadAsync(cancellationToken);
            packet.Data.CopyTo(buffer);
            return new SocketRead(packet.Data.Length, packet.End, packet.Type);
        }

        public Task CloseAsync(CancellationToken cancellationToken) { Closed = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { incoming.Writer.TryComplete(); sent.Writer.TryComplete(); return ValueTask.CompletedTask; }
        public void ReleaseAudio() => audioGate.TrySetResult();
        public void EmitClose() => incoming.Writer.TryWrite(([], true, WebSocketMessageType.Close));
        public void Emit(object payload, int fragment = int.MaxValue)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            for (var offset = 0; offset < bytes.Length; offset += fragment)
            {
                var chunk = bytes.Skip(offset).Take(fragment).ToArray();
                incoming.Writer.TryWrite((chunk, offset + chunk.Length >= bytes.Length, WebSocketMessageType.Text));
            }
        }
        public int CountSent(string type) => sentHistory.Count(json => JsonDocument.Parse(json).RootElement.GetProperty("type").GetString() == type);
        public async Task WaitSentAsync(string type)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (await sent.Reader.WaitToReadAsync(deadline.Token))
                while (sent.Reader.TryRead(out var json))
                    if (JsonDocument.Parse(json).RootElement.GetProperty("type").GetString() == type) return;
            throw new IOException("Expected fake provider send did not occur: " + type);
        }
    }

    private sealed class FakeCapture : IAudioCaptureSession
    {
        private readonly Channel<CaptureMessage> messages = Channel.CreateUnbounded<CaptureMessage>();
        public Guid SessionId { get; } = Guid.NewGuid();
        public Task StartAsync(CaptureSelection selection, ClockMapping clock, CancellationToken cancellationToken)
        { selection.Validate(); clock.Validate(); return Task.CompletedTask; }
        public IAsyncEnumerable<CaptureMessage> ReadAllAsync(CancellationToken cancellationToken) => messages.Reader.ReadAllAsync(cancellationToken);
        public void Emit(CaptureMessage message) => messages.Writer.TryWrite(message);
        public Task PauseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) { messages.Writer.TryComplete(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { messages.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
