using System.Runtime.CompilerServices;
using MeetingCompanion.Contracts;

namespace MeetingCompanion.Core;

/// <summary>Test-only finite replay adapter. Caller can inject timestamped capture events and failures.</summary>
public sealed class ReplayAudioSession(Guid sessionId, IEnumerable<CaptureMessage>? fixture = null) : IAudioCaptureSession
{
    private bool running;
    private bool disposed;
    public Guid SessionId { get; } = sessionId;

    public Task StartAsync(CaptureSelection selection, ClockMapping clock, CancellationToken cancellationToken)
    {
        Check(cancellationToken); selection.Validate(); clock.Validate(); running = true; return Task.CompletedTask;
    }

    public async IAsyncEnumerable<CaptureMessage> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        CaptureMessage[] defaults = [new AudioFrame(AudioStreamId.LocalMic, 0, 0, 20, AudioFormat.Normalized, 24000, new byte[960]),
            new AudioFrame(AudioStreamId.RemoteApp, 0, 0, 20, AudioFormat.Normalized, 24000, new byte[960])];
        foreach (var message in fixture ?? defaults)
        {
            Check(cancellationToken);
            if (!running) yield break;
            message.Validate(); yield return message;
            await Task.CompletedTask;
        }
    }

    public Task PauseAsync(CancellationToken cancellationToken) { Check(cancellationToken); running = false; return Task.CompletedTask; }
    public Task ResumeAsync(CancellationToken cancellationToken) { Check(cancellationToken); running = true; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken) { Check(cancellationToken); running = false; return Task.CompletedTask; }
    public ValueTask DisposeAsync() { running = false; disposed = true; return ValueTask.CompletedTask; }
    private void Check(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(disposed, this); }
}
