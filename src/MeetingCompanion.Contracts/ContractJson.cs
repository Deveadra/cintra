using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeetingCompanion.Contracts;

public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            MaxDepth = 32,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            AllowOutOfOrderMetadataProperties = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static byte[] SerializeEvent(ConversationEvent value)
    {
        value.Validate();
        return CheckSize(JsonSerializer.SerializeToUtf8Bytes(value, Options));
    }

    public static ConversationEvent DeserializeEvent(ReadOnlySpan<byte> json)
    {
        CheckInput(json);
        var value = JsonSerializer.Deserialize<ConversationEvent>(json, Options) ?? throw new ContractException("Null event.");
        value.Validate();
        return value;
    }

    public static byte[] SerializeIpc(IpcEnvelope value)
    {
        value.Validate();
        return CheckSize(JsonSerializer.SerializeToUtf8Bytes(value, Options));
    }

    public static IpcEnvelope DeserializeIpc(ReadOnlySpan<byte> json)
    {
        CheckInput(json);
        var value = JsonSerializer.Deserialize<IpcEnvelope>(json, Options) ?? throw new ContractException("Null IPC envelope.");
        value.Validate();
        return value;
    }

    public static byte[] SerializeSuggestion(Suggestion value, bool automatic)
    {
        value.Validate(automatic);
        return CheckSize(JsonSerializer.SerializeToUtf8Bytes(value, Options));
    }

    public static Suggestion DeserializeSuggestion(ReadOnlySpan<byte> json, bool automatic)
    {
        CheckInput(json);
        var value = JsonSerializer.Deserialize<Suggestion>(json, Options) ?? throw new ContractException("Null suggestion.");
        value.Validate(automatic);
        return value;
    }

    private static byte[] CheckSize(byte[] bytes) { CheckInput(bytes); return bytes; }
    private static void CheckInput(ReadOnlySpan<byte> json) =>
        Require.That(json.Length is > 0 and <= ContractVersion.MaxWireBytes, "Invalid wire payload size.");
}
