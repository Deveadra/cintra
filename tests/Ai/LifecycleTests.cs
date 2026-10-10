using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MeetingCompanion.Contracts;
using MeetingCompanion.Core;

namespace MeetingCompanion.Ai.Tests;

public sealed partial class VisionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotEvidenceIsResolvableAndExistingEventIdIsPreserved(bool preexisting)
    {
        var store = Store(); var metadata = Metadata(); var id = Guid.NewGuid();
        if (preexisting) store.Apply(new SnapshotAdded { SchemaVersion = 1, EventId = id, SessionId = store.GetContext(0).SessionId, ReceivedUtc = DateTimeOffset.UnixEpoch, Snapshot = metadata });
        var fake = Immediate(); using var bridge = new ManualVisionBridge(store, fake);
        await bridge.AnalyzeManualAsync(metadata, Png(), Consent);
        var entry = Assert.Single(store.GetSnapshot(metadata.CapturedAtMs).View.Events.OfType<SnapshotAdded>());
        using var summary = JsonDocument.Parse(fake.Last!.Reasoning.Context.Summary);
        Assert.Equal(entry.EventId, summary.RootElement.GetProperty("snapshot_event_id").GetGuid());
        if (preexisting) Assert.Equal(id, entry.EventId);
    }

    [Fact]
    public async Task ThrowingCancellationCallbackCannotBreakStop()
    {
        var fake = new Fake((_, ct) => { ct.Register(() => throw new InvalidOperationException("Defective adapter")); return new TaskCompletionSource<ReasoningResult>().Task; });
        using var bridge = new ManualVisionBridge(Store(), fake); var image = Png();
        var task = bridge.AnalyzeManualAsync(Metadata(), image, Consent); bridge.Stop();
        Assert.Equal("stopped", (await task).DiagnosticCode); Assert.All(image, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task UnknownLengthResponseCannotExceedReadBound()
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent() }));
        using var client = new HttpClient(handler); using var bridge = new ManualVisionBridge(Store(), new OpenAiVisionReasoner(client, () => "offline-placeholder", true));
        Assert.Equal("vision_response_too_large", (await bridge.AnalyzeManualAsync(Metadata(), Png(), Consent)).DiagnosticCode); Assert.Equal(1, handler.Calls);
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(new byte[70_000]).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(new byte[70_000]));
    }

    [Fact]
    public async Task ReceiptAfterImageTimeAndIssuesBasedOnItAreExcluded()
    {
        var store = Store(); var retained = Turn(store, 0, 20, "Available fact"); store.Apply(retained);
        var future = Turn(store, 20, 30, "Arrived after snapshot") with { ReceivedUtc = DateTimeOffset.UnixEpoch.AddMilliseconds(200) }; store.Apply(future);
        store.RecordIssue(new(Guid.NewGuid(), IssueField.ConfirmedFact, 30, new("Future derived fact", [new(EvidenceKind.Transcript, future.EventId, null, null)])));
        store.RecordIssue(new(Guid.NewGuid(), IssueField.ReportedError, 30, new("Available reported error", [new(EvidenceKind.Transcript, retained.EventId, null, null)])));
        var fake = Immediate(); using var bridge = new ManualVisionBridge(store, fake);
        await bridge.AnalyzeManualAsync(Metadata(100), Png(), Consent);
        Assert.Equal(retained.EventId, Assert.Single(fake.Last!.Reasoning.Context.SpeakerTurns).EventId);
        Assert.Empty(fake.Last.Reasoning.Context.ConfirmedFacts); Assert.Equal("Available reported error", fake.Last.Reasoning.Context.ReportedError!.Text);
        Assert.DoesNotContain("Future derived", JsonSerializer.Serialize(fake.Last.Reasoning.Context));
        Assert.Contains("\"received_after_capture_omitted\":true", fake.Last.Reasoning.Context.Summary);
    }

    [Fact]
    public async Task PartialAndHealthInterruptedTurnsAreExplicit()
    {
        var store = Store(); var turn = Turn(store, 0, 80, "Interrupted final"); store.Apply(turn);
        store.Apply(new SessionStateEvent
        {
            SchemaVersion = 1,
            EventId = Guid.NewGuid(),
            SessionId = turn.SessionId,
            ReceivedUtc = DateTimeOffset.UnixEpoch,
            State = SessionState.Degraded,
            Health = [new(Component.RemoteStt, HealthStatus.Disconnected, 40, "fixture")]
        });
        store.Apply(new TranscriptPartial
        {
            SchemaVersion = 1,
            EventId = Guid.NewGuid(),
            SessionId = turn.SessionId,
            ReceivedUtc = DateTimeOffset.UnixEpoch,
            StreamId = AudioStreamId.LocalMic,
            TurnId = Guid.NewGuid(),
            ProviderItemId = null,
            CapturedStartMs = 85,
            CapturedEndMs = 90,
            Text = "Incomplete partial",
            Attribution = Attribution.You,
            AttributionConfidence = AttributionConfidence.SourceConfirmed,
            ConfidenceNote = null
        });
        var fake = Immediate(); using var bridge = new ManualVisionBridge(store, fake);
        await bridge.AnalyzeManualAsync(Metadata(), Png(), Consent);
        Assert.Contains(turn.TurnId.ToString(), fake.Last!.Reasoning.Context.Summary); Assert.Contains("\"partial_turns_omitted\":true", fake.Last.Reasoning.Context.Summary);
        Assert.DoesNotContain("Incomplete partial", JsonSerializer.Serialize(fake.Last.Reasoning.Context));
    }

    [Fact]
    public async Task ContextCapAndPreCancelledRequestNeverDispatch()
    {
        var fake = Immediate(); using var bridge = new ManualVisionBridge(Store(), fake, new() { MaxContextCharacters = 512 });
        var image = Png(); Assert.Equal(ReasoningOutcome.Incomplete, (await bridge.AnalyzeManualAsync(Metadata(), image, Consent)).Outcome);
        Assert.All(image, b => Assert.Equal(0, b)); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        image = Png(); Assert.Equal("cancelled", (await bridge.AnalyzeManualAsync(Metadata(), image, Consent, cancel.Token)).DiagnosticCode);
        Assert.All(image, b => Assert.Equal(0, b)); Assert.Equal(0, fake.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdapterExceptionsAreIsolatedAndBudgetReservationIsConservative(bool cancelled)
    {
        var fake = new Fake((_, _) => cancelled ? Task.FromCanceled<ReasoningResult>(new CancellationToken(true)) : throw new InvalidOperationException("SECRET"));
        using var bridge = new ManualVisionBridge(Store(), fake); var image = Png();
        var result = await bridge.AnalyzeManualAsync(Metadata(), image, Consent);
        Assert.Null(result.Suggestion); Assert.Equal(ReasoningOutcome.Unavailable, result.Outcome); Assert.DoesNotContain("SECRET", result.DiagnosticCode);
        Assert.True(bridge.ReservedEstimateUsd > 0); Assert.All(image, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task HttpStopClearsSerializationBufferImmediatelyAndLateResponseIsIgnored()
    {
        byte[]? body = null; var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (message, ct) =>
        {
            var stream = (MemoryStream)await message.Content!.ReadAsStreamAsync(ct); body = stream.GetBuffer(); entered.SetResult();
            return await response.Task; // Deliberately ignores cancellation.
        });
        using var client = new HttpClient(handler); using var bridge = new ManualVisionBridge(Store(), new OpenAiVisionReasoner(client, () => "offline-placeholder", true));
        var image = Png(); var task = bridge.AnalyzeManualAsync(Metadata(), image, Consent); await entered.Task;
        bridge.Stop(); Assert.True(body!.All(b => b == 0)); Assert.All(image, b => Assert.Equal(0, b));
        Assert.Equal("stopped", (await task).DiagnosticCode); response.SetResult(Response("late malformed output"));
    }

    [Fact]
    public async Task RateLimitHonorsRetryAfterWithDeterministicClockAndRequiresNewManualImage()
    {
        var time = new ManualTime();
        var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler); using var bridge = new ManualVisionBridge(Store(), new OpenAiVisionReasoner(client, () => "offline-placeholder", true, time));
        var first = Metadata(); await bridge.AnalyzeManualAsync(first, Png(), Consent);
        time.Advance(TimeSpan.FromSeconds(119)); await bridge.AnalyzeManualAsync(Metadata(), Png(), Consent); Assert.Equal(1, handler.Calls);
        time.Advance(TimeSpan.FromSeconds(1)); Assert.Equal("snapshot_already_attempted", (await bridge.AnalyzeManualAsync(first, Png(), Consent)).DiagnosticCode);
        Assert.Equal(1, handler.Calls); await bridge.AnalyzeManualAsync(Metadata(), Png(), Consent); Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("extra_field")]
    [InlineData("prefix")]
    [InlineData("null_command")]
    [InlineData("length")]
    [InlineData("no_evidence")]
    public async Task ProviderShapeAndGroundingViolationsAreRejected(string problem)
    {
        var handler = new Handler(async (message, ct) =>
        {
            using var data = JsonDocument.Parse(await message.Content!.ReadAsStreamAsync(ct));
            using var input = JsonDocument.Parse(data.RootElement.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString()!);
            using var summary = JsonDocument.Parse(input.RootElement.GetProperty("context").GetProperty("summary").GetString()!);
            var id = summary.RootElement.GetProperty("snapshot_event_id").GetGuid();
            var values = new Dictionary<string, object?>
            {
                ["readable"] = true,
                ["observations"] = new[] { new { text = problem == "length" ? "Visible in snapshot:" + new string('x', 385) : problem == "prefix" ? "Definitely your server" : "Visible in snapshot: fixture", evidence_event_ids = problem == "no_evidence" ? Array.Empty<Guid>() : new[] { id } } },
                ["hypotheses"] = Array.Empty<string>(),
                ["next_steps"] = Array.Empty<string>(),
                ["commands"] = problem == "null_command" ? new object?[] { null } : Array.Empty<object>()
            };
            if (problem == "extra_field") values["environment"] = "Invented server";
            return Response(Envelope(values));
        });
        using var client = new HttpClient(handler); using var bridge = new ManualVisionBridge(Store(), new OpenAiVisionReasoner(client, () => "offline-placeholder", true));
        var result = await bridge.AnalyzeManualAsync(Metadata(), Png(), Consent);
        Assert.Equal(ReasoningOutcome.Incomplete, result.Outcome); Assert.Null(result.Suggestion); Assert.Equal(1, handler.Calls);
    }
}
