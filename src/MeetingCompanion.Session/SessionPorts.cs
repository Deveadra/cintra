using System.Diagnostics;
using MeetingCompanion.Contracts;
using MeetingCompanion.Platform;

namespace MeetingCompanion.Session;

public enum ProviderMode { Unavailable, OfflineTest, OpenAi }
public sealed record MeetingChoice(CallSourceKind Kind, CallAudioCandidate Candidate, AudioEndpoint Microphone);
public sealed record SourceChoices(CallAudioCandidate[] Applications, AudioEndpoint[] Microphones, string[] Diagnostics);
public sealed record SessionOptions
{
    public TimeSpan StartupDeadline { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan CleanupDeadline { get; init; } = TimeSpan.FromSeconds(15);
    public long HealthTtlMs { get; init; } = 2500;
}
public sealed record MeetingView(long Revision, Guid? SessionId, SessionState State, ProviderMode Provider,
    string Detail, ComponentHealth[] Health, double LocalPeak, double RemotePeak,
    TranscriptEvent[] Transcripts, AudioGapEvent[] Gaps, SourceChangedEvent[] SourceChanges,
    SnapshotMetadata? Snapshot, string? LastSource);
/// <summary>In-memory MC-009 seam; borrowed until ClearSnapshot/Stop. No vision or upload is performed.</summary>
public sealed record SnapshotHandoff(Guid SessionId, ClockMapping Clock, SnapshotMetadata Metadata,
    byte[] Png, ConversationContext Context);
public sealed record SnapshotTicket(Guid SessionId, long Generation, ClockMapping Clock);

public interface IMeetingTime
{
    ClockMapping CreateClock();
    long Now(ClockMapping clock);
}
public sealed class MeetingTime : IMeetingTime
{
    public ClockMapping CreateClock() => new(Stopwatch.GetTimestamp(), Stopwatch.Frequency, DateTimeOffset.UtcNow);
    public long Now(ClockMapping clock) => Math.Max(0,
        (long)((Stopwatch.GetTimestamp() - clock.MonotonicOriginTicks) * 1000d / clock.TicksPerSecond));
}

/// <summary>A single consumption owner. Observation reports actual capture messages, including levels.</summary>
public interface IMeetingRuntime : IAsyncDisposable
{
    Task RunAsync(CaptureSelection selection, ClockMapping clock, CancellationToken cancellationToken);
    IAsyncEnumerable<ConversationEvent> ReadAllAsync(CancellationToken cancellationToken);
    ComponentHealth[] SampleTransportHealth(long nowMs);
}
public interface IMeetingRuntimeFactory
{
    IMeetingRuntime Create(Guid sessionId, ProviderMode mode, Action<CaptureMessage> observe);
}
