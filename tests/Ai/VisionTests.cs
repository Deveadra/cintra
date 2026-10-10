using System.Net;
using System.Text;
using System.Text.Json;
using MeetingCompanion.Ai;
using MeetingCompanion.Contracts;
using MeetingCompanion.Core;

namespace MeetingCompanion.Ai.Tests;

public sealed partial class VisionTests
{
    private static readonly VisionConsent Consent = new(true, true, 1m);
    private static SnapshotMetadata Metadata(long at = 100) => new(Guid.NewGuid(), at, SnapshotSource.Region, "Fixture", new(-200, 40, 1, 1), 1, 1);
    // Valid 1x1 PNG: exact bytes travel over the one-shot adapter, never a file or URL.
    private static byte[] Png() => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jf1sAAAAASUVORK5CYII=");
    private static RollingConversationStore Store(ConversationRetention? retention = null) => new(Guid.NewGuid(), new(0, 1000, DateTimeOffset.UnixEpoch), retention);
    private static TranscriptFinal Turn(RollingConversationStore store, long start, long end, string text) => new()
    {
        SchemaVersion = 1,
        EventId = Guid.NewGuid(),
        SessionId = store.GetContext(0).SessionId,
        ReceivedUtc = DateTimeOffset.UnixEpoch,
        StreamId = AudioStreamId.RemoteApp,
        TurnId = Guid.NewGuid(),
        ProviderItemId = "fixture-item",
        CapturedStartMs = start,
        CapturedEndMs = end,
        Text = text,
        Attribution = Attribution.Remote,
        AttributionConfidence = AttributionConfidence.Inferred,
        ConfidenceNote = "Mixed remote source"
    };
    private static ReasoningResult Completed(VisionRequest r, Guid? evidence = null) => new(ReasoningOutcome.Completed,
        new(Guid.NewGuid(), SuggestionKind.ScreenshotAnalysis, Priority.Normal, "Snapshot",
            "Observed: Visible in snapshot: fixture error.\nPossible explanation: Unknown until confirmed.", [], [], Confidence.Low,
            evidence is Guid id ? [id] : [], ["Environment is unverified."], r.Snapshot.CapturedAtMs + 1000), null);

    private sealed class Fake(Func<VisionRequest, CancellationToken, Task<ReasoningResult>> handle) : IVisionReasoner
    {
        public int Calls { get; private set; }
        public VisionRequest? Last { get; private set; }
        public Task<ReasoningResult> AnalyzeAsync(VisionRequest request, CancellationToken token) { Calls++; Last = request; return handle(request, token); }
    }
    private static Fake Immediate() => new((r, _) => Task.FromResult(Completed(r)));

    [Fact]
    public async Task ManualOnlyAsOfCapturePreservesIdsAttributionCropAndGaps()
    {
        var store = Store();
        var early = Turn(store, 0, 80, "The fixture failed."); store.Apply(early);
        var late = Turn(store, 110, 120, "Secret later discussion"); store.Apply(late);
        var gap = new AudioGapEvent { SchemaVersion = 1, EventId = Guid.NewGuid(), SessionId = early.SessionId, ReceivedUtc = DateTimeOffset.UnixEpoch, Gap = new(AudioStreamId.RemoteApp, 20, 30, GapReason.NetworkLost, 240) }; store.Apply(gap);
        var fake = Immediate(); using var bridge = new ManualVisionBridge(store, fake);
        Assert.Equal(0, fake.Calls);
        var image = Png(); var expected = image.ToArray(); var metadata = Metadata();
        var result = await bridge.AnalyzeManualAsync(metadata, image, Consent);
        Assert.Equal(ReasoningOutcome.Completed, result.Outcome);
        Assert.Equal(1, fake.Calls);
        var sent = fake.Last!;
        Assert.Equal(metadata, sent.Snapshot); Assert.Equal(100, sent.Reasoning.Context.AsOfMs); Assert.False(sent.Reasoning.Automatic);
        var turn = Assert.Single(sent.Reasoning.Context.SpeakerTurns);
        Assert.Equal(early.EventId, turn.EventId); Assert.Equal(early.TurnId, turn.TurnId); Assert.Equal(early.StreamId, turn.StreamId);
        Assert.Equal(AttributionConfidence.Inferred, turn.AttributionConfidence);
        Assert.DoesNotContain("Secret later", JsonSerializer.Serialize(sent.Reasoning.Context));
        Assert.Contains(gap.EventId.ToString(), sent.Reasoning.Context.Summary); Assert.Contains(early.TurnId.ToString(), sent.Reasoning.Context.Summary);
        Assert.All(image, b => Assert.Equal(0, b)); Assert.All(sent.Image.ToArray(), b => Assert.Equal(0, b));
        Assert.NotEmpty(expected);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ConsentRejectsWithoutSendingAndClearsImage(bool api, bool imageConsent)
    {
        var fake = Immediate(); using var bridge = new ManualVisionBridge(Store(), fake); var image = Png();
        Assert.Equal("vision_consent_required", (await bridge.AnalyzeManualAsync(Metadata(), image, new(api, imageConsent, 1))).DiagnosticCode);
        Assert.Equal(0, fake.Calls); Assert.All(image, b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData("size")]
    [InlineData("signature")]
    [InlineData("dimensions")]
    [InlineData("crop")]
    [InlineData("time")]
    public async Task InvalidImageNeverSends(string failure)
    {
        var fake = Immediate(); using var bridge = new ManualVisionBridge(Store(), fake); var image = Png(); var metadata = Metadata();
        if (failure == "size") image = new byte[4 * 1024 * 1024 + 1];
        if (failure == "signature") image[0] = 0;
        if (failure == "dimensions") metadata = metadata with { Width = 2 };
        if (failure == "crop") metadata = metadata with { Crop = new(0, 0, 2, 1) };
        if (failure == "time") metadata = metadata with { CapturedAtMs = -1 };
        Assert.Equal(ReasoningOutcome.Incomplete, (await bridge.AnalyzeManualAsync(metadata, image, Consent)).Outcome);
        Assert.Equal(0, fake.Calls); Assert.All(image, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task MissingEvictedAndBoundedContextIsFlagged()
    {
        var store = Store(new() { MaxEvents = 2 });
        for (int i = 0; i < 10; i++) store.Apply(Turn(store, i * 10, i * 10 + 5, new string('x', 2000)));
        var fake = Immediate(); using var bridge = new ManualVisionBridge(store, fake);
        await bridge.AnalyzeManualAsync(Metadata(5), Png(), Consent);
        Assert.Empty(fake.Last!.Reasoning.Context.SpeakerTurns); Assert.Contains("context_may_be_missing_or_evicted", fake.Last.Reasoning.Context.Summary);
        await bridge.AnalyzeManualAsync(Metadata(100), Png(), Consent);
        Assert.Equal(2, fake.Last.Reasoning.Context.SpeakerTurns.Length); Assert.All(fake.Last.Reasoning.Context.SpeakerTurns, t => Assert.Equal(512, t.Text.Length));
    }

    [Fact]
    public async Task BusyStopAndLateCompletionCannotPublishOrRetainOwnedImage()
    {
        var pending = new TaskCompletionSource<ReasoningResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new Fake((_, _) => pending.Task); using var bridge = new ManualVisionBridge(Store(), fake);
        var image = Png(); var task = bridge.AnalyzeManualAsync(Metadata(), image, Consent);
        var other = Png(); Assert.Equal("busy", (await bridge.AnalyzeManualAsync(Metadata(), other, Consent)).DiagnosticCode);
        Assert.All(other, b => Assert.Equal(0, b)); bridge.Stop(); Assert.All(image, b => Assert.Equal(0, b));
        Assert.Equal("stopped", (await task).DiagnosticCode);
        pending.SetResult(Completed(fake.Last!));
        Assert.Equal("stopped", (await bridge.AnalyzeManualAsync(Metadata(), Png(), Consent)).DiagnosticCode); Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task CallerCancellationIgnoresUncooperativeProvider()
    {
        var fake = new Fake((_, _) => new TaskCompletionSource<ReasoningResult>().Task);
        using var bridge = new ManualVisionBridge(Store(), fake); using var cancel = new CancellationTokenSource(); var image = Png();
        var task = bridge.AnalyzeManualAsync(Metadata(), image, Consent, cancel.Token); cancel.Cancel();
        Assert.Equal("cancelled", (await task).DiagnosticCode); Assert.All(image, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task TimeoutUsesDeterministicClockAndNeverRetries()
    {
        var clock = new ManualTime(); var fake = new Fake((_, _) => new TaskCompletionSource<ReasoningResult>().Task);
        using var bridge = new ManualVisionBridge(Store(), fake, new() { Timeout = TimeSpan.FromSeconds(10) }, clock);
        var metadata = Metadata(); var image = Png(); var task = bridge.AnalyzeManualAsync(metadata, image, Consent);
        clock.Advance(TimeSpan.FromSeconds(10)); Assert.Equal(ReasoningOutcome.TimedOut, (await task).Outcome);
        Assert.All(image, b => Assert.Equal(0, b)); Assert.Equal("snapshot_already_attempted", (await bridge.AnalyzeManualAsync(metadata, Png(), Consent)).DiagnosticCode); Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task BudgetsRequestCapsAndStoppedStoreRejectBeforeUpload()
    {
        var fake = Immediate(); var store = Store(); using var bridge = new ManualVisionBridge(store, fake, new() { MaxRequests = 1 });
        Assert.Equal("estimated_budget_exceeded", (await bridge.AnalyzeManualAsync(Metadata(), Png(), Consent with { ApprovedEstimateUsd = 0 })).DiagnosticCode);
        Assert.Equal(0, bridge.ReservedEstimateUsd);
        await bridge.AnalyzeManualAsync(Metadata(), Png(), Consent);
        Assert.True(bridge.ReservedEstimateUsd > 0); Assert.Equal("request_limit", (await bridge.AnalyzeManualAsync(Metadata(), Png(), Consent)).DiagnosticCode);
        using var low = new ManualVisionBridge(Store(), fake, new() { SessionBudgetUsd = 0.0001m });
        Assert.Equal("estimated_budget_exceeded", (await low.AnalyzeManualAsync(Metadata(), Png(), Consent)).DiagnosticCode);
        store.Stop(); using var stopped = new ManualVisionBridge(store, fake);
        Assert.Equal("conversation_stopped", (await stopped.AnalyzeManualAsync(Metadata(), Png(), Consent)).DiagnosticCode); Assert.Equal(1, fake.Calls);
    }

    [Theory]
    [InlineData("evidence")]
    [InlineData("command")]
    [InlineData("identifier")]
    [InlineData("expiry")]
    [InlineData("body")]
    [InlineData("outcome")]
    [InlineData("failure_card")]
    public async Task InvalidEvidenceCommandsAndOutcomesNeverPublish(string failure)
    {
        var fake = new Fake((r, _) =>
        {
            var result = Completed(r); var s = result.Suggestion!;
            result = failure switch
            {
                "evidence" => result with { Suggestion = s with { EvidenceEventIds = [Guid.NewGuid()] } },
                "command" => result with { Suggestion = s with { Commands = [new("rm -rf /", CommandRisk.Modifying, false, [])] } },
                "identifier" => result with { Suggestion = s with { Commands = [new("kubectl logs invented-pod", CommandRisk.ReadOnly, false, [])] } },
                "expiry" => result with { Suggestion = s with { ExpiresAtMs = 0 } },
                "body" => result with { Suggestion = s with { Body = new string('x', 4000) } },
                "outcome" => result with { Outcome = (ReasoningOutcome)99 },
                _ => result with { Outcome = ReasoningOutcome.Refused }
            };
            return Task.FromResult(result);
        });
        using var bridge = new ManualVisionBridge(Store(), fake);
        var result = await bridge.AnalyzeManualAsync(Metadata(), Png(), Consent); Assert.Null(result.Suggestion); Assert.Equal(ReasoningOutcome.Incomplete, result.Outcome);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token) { Calls++; return handle(message, token); }
    }
    private static HttpResponseMessage Response(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private static string Envelope(object payload) => JsonSerializer.Serialize(new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = JsonSerializer.Serialize(payload) } } } }, usage = new { input_tokens = 123, output_tokens = 40 } });

    [Fact]
    public async Task ResponsesWireIsOneImageNoToolsNoStorageStrictSchemaAndReviewedTemplate()
    {
        byte[]? body = null; string? sentImage = null;
        var handler = new Handler(async (m, ct) =>
        {
            Assert.Equal("https://api.openai.com/v1/responses", m.RequestUri!.ToString()); Assert.Equal(HttpMethod.Post, m.Method);
            var bodyStream = (MemoryStream)await m.Content!.ReadAsStreamAsync(ct); body = bodyStream.GetBuffer(); using var data = JsonDocument.Parse(body);
            var root = data.RootElement; Assert.Equal(OpenAiVisionReasoner.Model, root.GetProperty("model").GetString());
            Assert.False(root.GetProperty("store").GetBoolean()); Assert.False(root.GetProperty("background").GetBoolean()); Assert.False(root.TryGetProperty("tools", out _));
            Assert.True(root.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
            var content = root.GetProperty("input")[0].GetProperty("content"); Assert.Equal(2, content.GetArrayLength());
            sentImage = content[1].GetProperty("image_url").GetString(); Assert.Equal("data:image/png;base64," + Convert.ToBase64String(Png()), sentImage);
            using var text = JsonDocument.Parse(content[0].GetProperty("text").GetString()!);
            using var summary = JsonDocument.Parse(text.RootElement.GetProperty("context").GetProperty("summary").GetString()!);
            var id = summary.RootElement.GetProperty("snapshot_event_id").GetGuid();
            return Response(Envelope(new { readable = true, observations = new[] { new { text = "Visible in snapshot: fixture error", evidence_event_ids = new[] { id } } }, hypotheses = new[] { "Unverified fixture failure" }, next_steps = new[] { "Confirm the namespace before copying" }, commands = new[] { new { text = "kubectl get pods --namespace <namespace>", risk = "read_only", is_template = true, required_inputs = new[] { "namespace" } } } }));
        });
        using var client = new HttpClient(handler); var provider = new OpenAiVisionReasoner(client, () => "offline-placeholder", true);
        using var bridge = new ManualVisionBridge(Store(), provider);
        var result = await bridge.AnalyzeManualAsync(Metadata(), Png(), Consent);
        Assert.Equal(ReasoningOutcome.Completed, result.Outcome); Assert.True(Assert.Single(result.Suggestion!.Commands).IsTemplate);
        Assert.Equal(1, handler.Calls); Assert.Equal(123, provider.LastInputTokens); Assert.Equal(40, provider.LastOutputTokens);
        Assert.True(body!.All(b => b == 0)); Assert.NotNull(sentImage);
    }

    [Theory]
    [InlineData(401, "vision_authentication_required")]
    [InlineData(429, "vision_rate_limited_no_retry")]
    [InlineData(500, "vision_http_failure_no_retry")]
    public async Task HttpFailuresAreSanitizedAndNeverReplayed(int status, string code)
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("SECRET provider error") }));
        using var client = new HttpClient(handler); using var bridge = new ManualVisionBridge(Store(), new OpenAiVisionReasoner(client, () => "offline-placeholder", true));
        var metadata = Metadata(); var result = await bridge.AnalyzeManualAsync(metadata, Png(), Consent);
        Assert.Equal(code, result.DiagnosticCode); Assert.Null(result.Suggestion);
        Assert.Equal("snapshot_already_attempted", (await bridge.AnalyzeManualAsync(metadata, Png(), Consent)).DiagnosticCode);
        if (status != 500) { await bridge.AnalyzeManualAsync(Metadata(), Png(), Consent); Assert.Equal(1, handler.Calls); }
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("refusal")]
    [InlineData("incomplete")]
    [InlineData("oversize")]
    [InlineData("invalid_evidence")]
    [InlineData("unreadable")]
    [InlineData("null_fields")]
    [InlineData("network")]
    public async Task ProviderNegativePathsAreBoundedAndNeverRetried(string failure)
    {
        var handler = new Handler((_, _) =>
        {
            if (failure == "network") throw new HttpRequestException("SECRET URL/token");
            string text = failure switch
            {
                "malformed" => "not json SECRET",
                "refusal" => "{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"refusal\",\"refusal\":\"SECRET\"}]}]}",
                "incomplete" => "{\"status\":\"incomplete\"}",
                "oversize" => new string('x', 70_000),
                "null_fields" => Envelope(new { readable = true, observations = (object?)null, hypotheses = (object?)null, next_steps = (object?)null, commands = (object?)null }),
                _ => Envelope(new { readable = failure != "unreadable", observations = new[] { new { text = "Invented", evidence_event_ids = new[] { Guid.NewGuid() } } }, hypotheses = Array.Empty<string>(), next_steps = Array.Empty<string>(), commands = Array.Empty<object>() })
            };
            return Task.FromResult(Response(text));
        });
        using var client = new HttpClient(handler); using var bridge = new ManualVisionBridge(Store(), new OpenAiVisionReasoner(client, () => "offline-placeholder", true));
        var result = await bridge.AnalyzeManualAsync(Metadata(), Png(), Consent);
        Assert.NotEqual(ReasoningOutcome.Completed, result.Outcome); Assert.Null(result.Suggestion); Assert.DoesNotContain("SECRET", result.DiagnosticCode); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AdapterConsentAndMissingCredentialNeverSend()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("Must not send")); using var client = new HttpClient(handler);
        using var disabled = new ManualVisionBridge(Store(), new OpenAiVisionReasoner(client, () => "offline-placeholder", false));
        Assert.NotEqual(ReasoningOutcome.Completed, (await disabled.AnalyzeManualAsync(Metadata(), Png(), Consent)).Outcome);
        using var noKey = new ManualVisionBridge(Store(), new OpenAiVisionReasoner(client, () => null, true));
        Assert.Equal("vision_authentication_required", (await noKey.AnalyzeManualAsync(Metadata(), Png(), Consent)).DiagnosticCode); Assert.Equal(0, handler.Calls);
    }

    private sealed class ManualTime : TimeProvider
    {
        private readonly List<Timer> timers = [];
        private TimeSpan elapsed;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + elapsed;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new Timer(this, callback, state); timer.Change(dueTime, period); timers.Add(timer); return timer; }
        public void Advance(TimeSpan delta) { elapsed += delta; foreach (var timer in timers.ToArray()) timer.Fire(); }
        private sealed class Timer(ManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            private TimeSpan? due;
            public bool Change(TimeSpan dueTime, TimeSpan period) { due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.elapsed + dueTime; return true; }
            public void Fire() { if (due is TimeSpan at && owner.elapsed >= at) { due = null; callback(state); } }
            public void Dispose() => due = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
