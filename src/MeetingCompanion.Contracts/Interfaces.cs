namespace MeetingCompanion.Contracts;

/// <summary>One owner per session. Dispose/Stop release capture and bounded buffers. Never call from an audio callback.</summary>
public interface IAudioCaptureSession : IAsyncDisposable
{
    Guid SessionId { get; }
    Task StartAsync(CaptureSelection selection, ClockMapping clock, CancellationToken cancellationToken);
    IAsyncEnumerable<CaptureMessage> ReadAllAsync(CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task ResumeAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>Exactly one source and independent transport per instance; accepts no images or reasoning requests.</summary>
public interface ITranscriptionSession : IAsyncDisposable
{
    Guid SessionId { get; }
    AudioStreamId StreamId { get; }
    Task StartAsync(CancellationToken cancellationToken);
    ValueTask AppendAsync(AudioFrame frame, CancellationToken cancellationToken);
    Task CommitTurnAsync(Guid turnId, long capturedStartMs, long capturedEndMs, CancellationToken cancellationToken);
    IAsyncEnumerable<ConversationEvent> ReadAllAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface IConversationStore
{
    void Apply(ConversationEvent conversationEvent);
    ConversationContext GetContext(long asOfMs);
    void Clear();
}

public interface ISuggestionGenerator
{
    Task<ReasoningResult> GenerateAsync(SuggestionRequest request, CancellationToken cancellationToken);
}

public interface IVisionReasoner
{
    Task<ReasoningResult> AnalyzeAsync(VisionRequest request, CancellationToken cancellationToken);
}

public sealed record CallAudioCandidate(string Id, string AppDisplayName, int ProcessId, int[] ProcessTree,
    string ExecutablePath, string[] AudioSessionIds, bool RenderActive, long? WindowHandle, Confidence Confidence, string SelectionHint);
public enum ProbeOutcome { AudioDetected, Silence, NotSupported, DeviceUnavailable, Ambiguous }
public sealed record SourceProbeResult(ProbeOutcome Outcome, string DiagnosticCode);
public sealed record ResolvedCallSource(CaptureSource Source, string ReconnectionHint);
public interface ICallSourceAdapter
{
    string Id { get; }
    Task<IReadOnlyList<CallAudioCandidate>> DiscoverAsync(CancellationToken cancellationToken);
    Task<SourceProbeResult> ProbeAsync(CallAudioCandidate candidate, CancellationToken cancellationToken);
    Task<ResolvedCallSource> ResolveAsync(CallAudioCandidate candidate, CancellationToken cancellationToken);
}
