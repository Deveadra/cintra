using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeetingCompanion.Contracts;

namespace MeetingCompanion.Contract.Tests;

public sealed class WireContractTests
{
    private static readonly Guid Id = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private const string GoldenFinal = """
        {"schema_version":1,"event_id":"10000000-0000-0000-0000-000000000001","session_id":"10000000-0000-0000-0000-000000000001","stream_id":"remote_app","event_type":"transcript_final","turn_id":"10000000-0000-0000-0000-000000000001","provider_item_id":null,"captured_start_ms":100,"captured_end_ms":120,"received_utc":"2026-10-09T14:10:15Z","text":"Synthetic question?","attribution":"REMOTE","attribution_confidence":"source_confirmed","confidence_note":null}
        """;

    private static IpcEnvelope Envelope(CaptureMessage payload) => new(1, Id, Id, payload);
    private static AudioFrame Frame(AudioStreamId stream = AudioStreamId.LocalMic) => new(stream, 0, 0, 20, AudioFormat.Normalized, 48000, new byte[960]);

    [Fact]
    public void GoldenTranscriptRoundTripsWithExactPublicNames()
    {
        var transcript = Assert.IsType<TranscriptFinal>(ContractJson.DeserializeEvent(Encoding.UTF8.GetBytes(GoldenFinal)));
        Assert.Equal(AudioStreamId.RemoteApp, transcript.StreamId);
        using var json = JsonDocument.Parse(ContractJson.SerializeEvent(transcript));
        Assert.Equal("transcript_final", json.RootElement.GetProperty("event_type").GetString());
        Assert.Equal("REMOTE", json.RootElement.GetProperty("attribution").GetString());
        Assert.Equal("source_confirmed", json.RootElement.GetProperty("attribution_confidence").GetString());
        Assert.Equal(transcript, ContractJson.DeserializeEvent(ContractJson.SerializeEvent(transcript)));
    }

    [Theory]
    [InlineData("\"schema_version\":1", "\"schema_version\":2")]
    [InlineData("\"captured_end_ms\":120", "\"captured_end_ms\":99")]
    [InlineData("\"attribution\":\"REMOTE\"", "\"attribution\":\"YOU\"")]
    [InlineData("\"stream_id\":\"remote_app\"", "\"stream_id\":\"endpoint_fallback\"")]
    public void InvalidTranscriptSemanticsRejected(string from, string to) =>
        Assert.Throws<ContractException>(() => ContractJson.DeserializeEvent(Encoding.UTF8.GetBytes(GoldenFinal.Replace(from, to))));

    [Theory]
    [InlineData("\"stream_id\":\"remote_app\"", "\"stream_id\":22")]
    [InlineData("\"event_type\":\"transcript_final\"", "\"event_type\":\"invented\"")]
    [InlineData("\"text\":\"Synthetic question?\"", "\"text\":null")]
    [InlineData("\"schema_version\":1,", "")]
    public void InvalidJsonContractsRejected(string from, string to) =>
        Assert.Throws<JsonException>(() => ContractJson.DeserializeEvent(Encoding.UTF8.GetBytes(GoldenFinal.Replace(from, to))));

    [Fact]
    public void OptionalUnknownFieldIsForwardCompatible()
    {
        var json = GoldenFinal.Insert(1, "\"future_optional\":true,");
        Assert.IsType<TranscriptFinal>(ContractJson.DeserializeEvent(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData(AudioStreamId.LocalMic)]
    [InlineData(AudioStreamId.RemoteApp)]
    [InlineData(AudioStreamId.EndpointFallback)]
    public void PcmRoundTripPreservesStreamAndBytes(AudioStreamId stream)
    {
        var original = Frame(stream);
        original.Pcm[7] = 42;
        var copy = Assert.IsType<AudioFrame>(ContractJson.DeserializeIpc(ContractJson.SerializeIpc(Envelope(original))).Payload);
        Assert.Equal(stream, copy.StreamId); Assert.Equal(original.Pcm, copy.Pcm); Assert.Equal(original.Format, copy.Format);
    }

    [Fact]
    public void InvalidAudioAndFallbackAreRejected()
    {
        Assert.Throws<ContractException>(() => (Frame() with { Pcm = new byte[3] }).Validate());
        Assert.Throws<ContractException>(() => (Frame() with { Pcm = new byte[48002] }).Validate());
        Assert.Throws<ContractException>(() => (Frame() with { Format = new(48000, 2, 16) }).Validate());
        Assert.Throws<ContractException>(() => (Frame() with { CapturedEndMs = 200 }).Validate());
        Assert.Throws<ContractException>(() => new CaptureSource(AudioStreamId.EndpointFallback, "System output (unisolated)", "device", null, false, false).Validate());
        Assert.Throws<ContractException>(() => (Envelope(Frame()) with { SchemaVersion = 2 }).Validate());
        Assert.Throws<ContractException>(() => ContractJson.DeserializeIpc(new byte[ContractVersion.MaxWireBytes + 1]));
        Assert.Throws<JsonException>(() => ContractJson.DeserializeIpc("{}"u8));
    }

    [Fact]
    public void EveryIpcVariantRoundTrips()
    {
        var local = new CaptureSource(AudioStreamId.LocalMic, "Synthetic microphone", "fake", null, false, false);
        var remote = new CaptureSource(AudioStreamId.RemoteApp, "Synthetic app", null, 123, true, false);
        CaptureMessage[] messages = [new StartCapture(new(local, remote), new(0, 1000, DateTimeOffset.UnixEpoch)),
            new CaptureControl(CaptureAction.Pause), new CaptureControl(CaptureAction.Resume), new CaptureControl(CaptureAction.Stop),
            new CaptureHealth(AudioStreamId.LocalMic, new(Component.LocalCapture, HealthStatus.Silence, 0, null), 0, 0, 0),
            new CaptureGap(AudioStreamId.RemoteApp, 0, 1, GapReason.HelperExited, 1), new CaptureSourceChanged(remote, 1)];
        foreach (var message in messages)
            Assert.Equal(message, ContractJson.DeserializeIpc(ContractJson.SerializeIpc(Envelope(message))).Payload);
    }

    [Fact]
    public void AutomaticCommandsMustBeReadOnlyAndUnknownInputsRemainTemplates()
    {
        var suggestion = new Suggestion(Id, SuggestionKind.DiagnosticStep, Priority.Normal, "Inspect", "Inspect a synthetic workload.",
            [new("kubectl describe pod <pod>", CommandRisk.ReadOnly, true, ["pod"])], [], Confidence.Low, [Id], [], 100);
        suggestion.Validate(automatic: true);
        Assert.Throws<ContractException>(() => (suggestion with { Commands = [new("modify", CommandRisk.Modifying, false, [])] }).Validate(automatic: true));
        Assert.Throws<ContractException>(() => (suggestion with { Commands = [new("inspect <pod>", CommandRisk.ReadOnly, false, ["pod"])] }).Validate());
        Assert.Throws<ContractException>(() => (suggestion with { EvidenceEventIds = [], Assumptions = [] }).Validate());
    }

    [Fact]
    public void EveryEventVariantHasTypedRoundTrip()
    {
        var remote = new CaptureSource(AudioStreamId.RemoteApp, "Synthetic app", null, 123, true, false);
        var suggestion = new Suggestion(Id, SuggestionKind.SuggestedReply, Priority.Normal, "Reply", "Synthetic reply",
            [], [], Confidence.Low, [Id], [], 100);
        (string Kind, string Property, object Payload)[] cases = [
            ("audio_gap", "gap", new CaptureGap(AudioStreamId.RemoteApp, 0, 1, GapReason.NetworkLost, 0)),
            ("source_changed", "change", new CaptureSourceChanged(remote, 1)),
            ("snapshot_added", "snapshot", new SnapshotMetadata(Id, 1, SnapshotSource.Region, "Synthetic", new(0, 0, 20, 20), 20, 20)),
            ("suggestion_generated", "suggestion", suggestion),
            ("session_state", "state", SessionState.Degraded),
            ("trigger_detected", "kind", TriggerKind.DirectQuestion)];
        foreach (var (kind, property, payload) in cases)
        {
            var node = new JsonObject
            {
                ["schema_version"] = 1,
                ["event_id"] = Id,
                ["session_id"] = Id,
                ["received_utc"] = "2026-10-09T00:00:00+00:00",
                ["event_type"] = kind,
                [property] = JsonSerializer.SerializeToNode(payload, payload.GetType(), ContractJson.Options)
            };
            if (kind == "session_state") node["health"] = new JsonArray();
            if (kind == "trigger_detected")
            {
                node["trigger_id"] = Id;
                node["evidence_event_ids"] = new JsonArray(JsonValue.Create(Id));
            }
            var parsed = ContractJson.DeserializeEvent(Encoding.UTF8.GetBytes(node.ToJsonString()));
            Assert.True(JsonNode.DeepEquals(node, JsonNode.Parse(ContractJson.SerializeEvent(parsed))));
        }
        var partial = GoldenFinal.Replace("transcript_final", "transcript_partial");
        Assert.IsType<TranscriptPartial>(ContractJson.DeserializeEvent(Encoding.UTF8.GetBytes(partial)));
    }

    [Fact]
    public void SuggestionWirePolicyIsExplicitAtBoundary()
    {
        var suggestion = new Suggestion(Id, SuggestionKind.DiagnosticStep, Priority.Normal, "Synthetic", "Synthetic change",
            [new("example change", CommandRisk.Modifying, false, [])], [], Confidence.Low, [], ["Synthetic assumption"], 100);
        var bytes = ContractJson.SerializeSuggestion(suggestion, automatic: false);
        Assert.Throws<ContractException>(() => ContractJson.DeserializeSuggestion(bytes, automatic: true));
        Assert.Throws<ContractException>(() => ContractJson.SerializeSuggestion(suggestion, automatic: true));
        Assert.Equal(CommandRisk.Modifying, ContractJson.DeserializeSuggestion(bytes, automatic: false).Commands[0].Risk);
        Assert.Throws<ContractException>(() => ContractJson.DeserializeSuggestion("null"u8, automatic: false));
    }
}
