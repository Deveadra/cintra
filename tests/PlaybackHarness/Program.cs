using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using MeetingCompanion.Contracts;
using MeetingCompanion.Core;

if (args is ["--export-schemas", var directory])
{
    Directory.CreateDirectory(directory);
    foreach (var (type, name) in new[] { (typeof(IpcEnvelope), "ipc-v1"), (typeof(ConversationEvent), "events-v1"), (typeof(Suggestion), "suggestion-v1") })
    {
        var schema = ContractJson.Options.GetJsonSchemaAsNode(type);
        schema["type"] = "object"; // Public codec rejects a null root.
        FreezeVersion(schema);
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        schema["$id"] = $"urn:meeting-companion:{name}";
        File.WriteAllText(Path.Combine(directory, name + ".schema.json"), schema.ToJsonString(new() { WriteIndented = true }) + "\n");
    }
    return;
}

static void FreezeVersion(JsonNode? node)
{
    if (node is JsonObject obj)
    {
        if (obj["properties"] is JsonObject properties && properties["schema_version"] is JsonObject version)
            version["const"] = ContractVersion.Current;
        foreach (var property in obj.ToArray()) FreezeVersion(property.Value);
    }
    else if (node is JsonArray array)
        foreach (var item in array) FreezeVersion(item);
}

// Virtual time: no hardware, sockets, API credentials, or wall-clock scheduling.
var machine = new SessionStateMachine();
var transitions = new List<string>();
void Send(SessionSignal signal, long at) => transitions.Add($"{at}:{machine.Dispatch(signal, at).State}");
Send(SessionSignal.SelectSource, 0);
Send(SessionSignal.SourcesSelected, 0);
Send(SessionSignal.Start, 0);
var sessionId = Guid.Parse("10000000-0000-0000-0000-000000000001");
await using var audio = new ReplayAudioSession(sessionId);
await audio.StartAsync(new(new(AudioStreamId.LocalMic, "Synthetic mic", "fake", null, false, false),
    new(AudioStreamId.RemoteApp, "Synthetic incoming", null, 123, true, false)), new(0, 1000, DateTimeOffset.UnixEpoch), default);
var frames = new List<AudioFrame>();
await foreach (var item in audio.ReadAllAsync(default))
    if (item is AudioFrame frame) frames.Add(frame);
if (frames.Count != 2 || frames[0].StreamId == frames[1].StreamId) throw new InvalidOperationException("Replay stream separation failed.");
foreach (var component in new[] { Component.LocalCapture, Component.RemoteCapture, Component.LocalStt, Component.RemoteStt })
    machine.ReportHealth(new(component, HealthStatus.Healthy, 20, null), 20);
Send(SessionSignal.Started, 20);
transitions.Add($"2020:{machine.Tick(2020).State}");
Send(SessionSignal.Recover, 2021);
foreach (var component in new[] { Component.LocalCapture, Component.RemoteCapture, Component.LocalStt, Component.RemoteStt })
    machine.ReportHealth(new(component, HealthStatus.Healthy, 2022, null), 2022);
Send(SessionSignal.Recovered, 2022);
Send(SessionSignal.Pause, 2023);
await audio.PauseAsync(default);
Send(SessionSignal.Stop, 2024);
await audio.StopAsync(default);
Send(SessionSignal.Stopped, 2025);
string[] expected = ["0:SelectingSource", "0:Ready", "0:Starting", "20:Listening", "2020:Degraded", "2021:Recovering", "2022:Listening", "2023:Paused", "2024:Stopping", "2025:Stopped"];
if (!transitions.SequenceEqual(expected)) throw new InvalidOperationException("Replay lifecycle mismatch.");
Console.WriteLine(JsonSerializer.Serialize(new { mode = "offline_synthetic", frames = frames.Count, transitions, result = "PASS" }));
