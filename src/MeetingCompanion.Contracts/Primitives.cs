using System.Text.Json.Serialization;

namespace MeetingCompanion.Contracts;

public enum AudioStreamId { LocalMic, RemoteApp, EndpointFallback }
public enum Attribution
{
    [JsonStringEnumMemberName("YOU")] You,
    [JsonStringEnumMemberName("REMOTE")] Remote,
    [JsonStringEnumMemberName("MIXED")] Mixed,
    [JsonStringEnumMemberName("UNKNOWN")] Unknown
}
public enum AttributionConfidence { SourceConfirmed, Inferred, Unknown }
public enum SessionState { Stopped, SelectingSource, Ready, Starting, Listening, Degraded, Recovering, Paused, Stopping, Error }
public enum Component { LocalCapture, RemoteCapture, LocalStt, RemoteStt, Reasoning, SnapshotSend }
public enum HealthStatus { Unknown, Starting, Healthy, Silence, NoDevice, WrongProcess, ZeroLevel, PermissionDenied, Disconnected, Reconnecting, Failed, Stopped }
public enum GapReason { QueueOverflow, DeviceLost, SourceChanged, HelperExited, NetworkLost, Paused, Unknown }
public enum TriggerKind { DirectQuestion, TechnicalBlocker, RequestForSolution, SpecificError, SnapshotRequested, ManualAsk }
public enum SuggestionKind { SuggestedReply, DiagnosticStep, ContextClarification, ScreenshotAnalysis }
public enum Priority { Low, Normal, High }
public enum Confidence { Low, Moderate, High }
public enum CommandRisk { ReadOnly, Modifying, Unknown }
public enum EvidenceKind { Transcript, Snapshot, ToolResult, Assumption }
public enum SnapshotSource { ForegroundWindow, SelectedWindow, Region, Display }

public static class ContractVersion
{
    public const int Current = 1;
    public const int MaxWireBytes = 1_048_576;
    public const int MaxAudioBytes = 48_000; // One second of normalized mono PCM16.
}

public sealed class ContractException(string message) : Exception(message);

internal static class Require
{
    public static void That(bool condition, string message)
    {
        if (!condition) throw new ContractException(message);
    }

    public static void Id(Guid id) => That(id != Guid.Empty, "An identifier must not be empty.");
    public static void Text(string? text, int max = 16_384) =>
        That(!string.IsNullOrWhiteSpace(text) && text.Length <= max, "Text is missing or exceeds its limit.");
    public static void Range(long start, long end) => That(start >= 0 && end >= start, "Invalid capture interval.");
    public static void Enum<T>(T value) where T : struct, Enum => That(System.Enum.IsDefined(value), "Unknown enum value.");
}

/// <summary>Session-relative monotonic milliseconds. Never order turns by receipt time.</summary>
public sealed record ClockMapping(long MonotonicOriginTicks, long TicksPerSecond, DateTimeOffset UtcOrigin)
{
    public void Validate()
    {
        Require.That(MonotonicOriginTicks >= 0 && TicksPerSecond > 0, "Invalid clock mapping.");
        Require.That(UtcOrigin.Offset == TimeSpan.Zero, "Clock origin must be UTC.");
    }
}

public sealed record ComponentHealth(Component Component, HealthStatus Status, long ObservedAtMs, string? DiagnosticCode)
{
    public void Validate()
    {
        Require.Enum(Component);
        Require.Enum(Status);
        Require.That(ObservedAtMs >= 0, "Invalid health timestamp.");
        Require.That(DiagnosticCode is null || DiagnosticCode.Length <= 128, "Diagnostic code too long.");
    }
}
