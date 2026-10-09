using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using MeetingCompanion.Contracts;

namespace MeetingCompanion.Audio;

/// <summary>Owns one native helper. No fallback, audio file writes, provider calls or source discovery.</summary>
public sealed class NativeAudioCaptureSession(string helperPath) : IAudioCaptureSession
{
    private readonly SemaphoreSlim lifecycle = new(1);
    private readonly SemaphoreSlim writer = new(1);
    private readonly CaptureInbox inbox = new();
    private readonly CancellationTokenSource lifetime = new();
    private NamedPipeServerStream? pipe;
    private Process? helper;
    private Task? reader;
    private ClockMapping? clock;
    private bool started;
    private bool disposed;
    private volatile bool acceptingAudio;
    private volatile bool stopping;
    private volatile bool stopRequested;
    private readonly object acknowledgementGate = new();
    private HashSet<AudioStreamId> stoppedSources = new();
    private readonly HashSet<AudioStreamId> terminalSources = new();
    private TaskCompletionSource acknowledged = NewAcknowledgement();
    private long localSequence = -1;
    private long remoteSequence = -1;
    public Guid SessionId { get; } = Guid.NewGuid();

    private static TaskCompletionSource NewAcknowledgement() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long Now => clock is null ? 0 : Math.Max(0, (long)((Stopwatch.GetTimestamp() - clock.MonotonicOriginTicks) * 1000d / clock.TicksPerSecond));

    public async Task StartAsync(CaptureSelection selection, ClockMapping mapping, CancellationToken cancellationToken)
    {
        selection.Validate(); mapping.Validate();
        if (selection.Incoming.StreamId != AudioStreamId.RemoteApp)
            throw new NotSupportedException("The native helper supports selected process-tree incoming audio only.");
        if (mapping.TicksPerSecond != Stopwatch.Frequency || mapping.MonotonicOriginTicks > Stopwatch.GetTimestamp())
            throw new ArgumentException("Capture requires the Windows QPC clock mapping.", nameof(mapping));
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started || stopRequested) throw new InvalidOperationException("Create a new capture session after Stop; Start is single-use.");
            started = true; clock = mapping;
            if (!Path.IsPathFullyQualified(helperPath) || !File.Exists(helperPath)) throw new FileNotFoundException("Native helper path must identify the built executable.", helperPath);
            var name = $"cintra-audio-{SessionId:N}";
            var sid = WindowsIdentity.GetCurrent().User ?? throw new IOException("Current user SID unavailable.");
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
            pipe = CreateLocalPipe(name, security);
            var info = new ProcessStartInfo(helperPath) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--pipe"); info.ArgumentList.Add(name); info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            helper = Process.Start(info) ?? throw new IOException("Could not start native capture helper.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            await pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var client) || client != helper.Id)
                throw new IOException("Named-pipe client does not match the launched native helper.");
            if (stopRequested) throw new OperationCanceledException("Stop requested during capture startup.");
            acceptingAudio = true;
            await SendAsync(new StartCapture(selection, mapping), deadline.Token).ConfigureAwait(false);
            reader = ReadLoopAsync();
        }
        catch
        {
            await ReleaseHelperAsync().ConfigureAwait(false);
            inbox.Complete(clear: true);
            throw;
        }
        finally { lifecycle.Release(); }
    }

    public IAsyncEnumerable<CaptureMessage> ReadAllAsync(CancellationToken cancellationToken) => inbox.ReadAllAsync(cancellationToken);

    private async Task SendAsync(CaptureMessage message, CancellationToken cancellationToken)
    {
        await writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (pipe is null || !pipe.IsConnected) throw new IOException("Native capture pipe disconnected.");
            await IpcFraming.WriteAsync(pipe, new IpcEnvelope(1, Guid.NewGuid(), SessionId, message), TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        finally { writer.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        var seen = new Dictionary<AudioStreamId, long> { [AudioStreamId.LocalMic] = Now, [AudioStreamId.RemoteApp] = Now };
        var status = new Dictionary<AudioStreamId, HealthStatus> { [AudioStreamId.LocalMic] = HealthStatus.Starting, [AudioStreamId.RemoteApp] = HealthStatus.Starting };
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var envelope = await IpcFraming.ReadAsync(pipe!, TimeSpan.FromSeconds(2), lifetime.Token).ConfigureAwait(false);
                if (envelope is null) break;
                if (envelope.SessionId != SessionId) throw new IOException("Native helper session mismatch.");
                var message = envelope.Payload;
                var messageStream = message switch
                {
                    AudioFrame audio => audio.StreamId,
                    CaptureHealth streamHealth => streamHealth.StreamId,
                    CaptureGap gap => gap.StreamId,
                    CaptureSourceChanged changed => changed.Source.StreamId,
                    _ => (AudioStreamId?)null
                };
                if (messageStream is { } stream)
                {
                    if (stream == AudioStreamId.EndpointFallback) throw new IOException("Unselected endpoint fallback.");
                    seen[stream] = message is CaptureHealth observedHealth ? Math.Min(Now, observedHealth.Health.ObservedAtMs) : Now;
                    if (message is CaptureHealth statusHealth) status[stream] = statusHealth.Health.Status;
                    else if (message is AudioFrame) status[stream] = HealthStatus.Healthy;
                }
                foreach (var pair in seen)
                {
                    var limit = status[pair.Key] == HealthStatus.Starting ? 8000 : 2000;
                    if (status[pair.Key] != HealthStatus.Stopped && Now - pair.Value > limit)
                        throw new IOException("Individual capture worker heartbeat expired.");
                }
                if (message is StartCapture or CaptureControl) throw new IOException("Unexpected native command response.");
                if (message is AudioFrame frame)
                {
                    if (frame.StreamId == AudioStreamId.EndpointFallback) throw new IOException("Unselected endpoint fallback.");
                    ref var sequence = ref (frame.StreamId == AudioStreamId.LocalMic ? ref localSequence : ref remoteSequence);
                    if (frame.Sequence <= sequence) throw new IOException("Native sequence went backwards.");
                    sequence = frame.Sequence;
                    if (!acceptingAudio) continue;
                    if (Now - frame.CapturedStartMs > 2000)
                    {
                        inbox.Add(new CaptureGap(frame.StreamId, frame.CapturedStartMs, frame.CapturedEndMs, GapReason.QueueOverflow, frame.Pcm.Length / 2));
                        continue;
                    }
                }
                if (message is CaptureHealth health)
                {
                    lock (acknowledgementGate)
                    {
                        if (health.Health.Status == HealthStatus.Stopped) stoppedSources.Add(health.StreamId);
                        else stoppedSources.Remove(health.StreamId);
                        if (health.Health.DiagnosticCode == "capture_resources_released") terminalSources.Add(health.StreamId);
                        if (stoppedSources.Contains(AudioStreamId.LocalMic) && stoppedSources.Contains(AudioStreamId.RemoteApp)) acknowledged.TrySetResult();
                    }
                }
                inbox.Add(message);
            }
            if (!stopping && !lifetime.IsCancellationRequested) throw new IOException("Native helper exited.");
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or ContractException or System.Text.Json.JsonException)
        {
            if (!stopping && !lifetime.IsCancellationRequested)
            {
                acceptingAudio = false; inbox.Clear();
                foreach (var stream in new[] { AudioStreamId.LocalMic, AudioStreamId.RemoteApp })
                {
                    inbox.Add(new CaptureGap(stream, Now, Now, GapReason.HelperExited, 0));
                    inbox.Add(new CaptureHealth(stream, new ComponentHealth(stream == AudioStreamId.LocalMic ? Component.LocalCapture : Component.RemoteCapture,
                        HealthStatus.Disconnected, Now, "native_helper_exited_or_heartbeat_timeout"), 0, 0, 0));
                }
                pipe?.Dispose();
                KillOwnedHelper();
                inbox.Complete();
            }
            acknowledged.TrySetException(new IOException("Capture helper failed before cleanup acknowledgement.", error));
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActive(); acceptingAudio = false; inbox.ClearAudio(); ResetAcknowledgements();
            await SendAsync(new CaptureControl(CaptureAction.Pause), cancellationToken).ConfigureAwait(false);
            await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            inbox.ClearAudio();
        }
        catch { await FailControlAsync().ConfigureAwait(false); throw; }
        finally { lifecycle.Release(); }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActive(); inbox.ClearAudio(); ResetAcknowledgements();
            acceptingAudio = true;
            await SendAsync(new CaptureControl(CaptureAction.Resume), cancellationToken).ConfigureAwait(false);
        }
        catch { await FailControlAsync().ConfigureAwait(false); throw; }
        finally { lifecycle.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Cleanup must run even when a caller cancels its wait for graceful Stop.
        stopRequested = true;
        acceptingAudio = false;
        await lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        Exception? failure = null;
        try
        {
            if (stopping || !started) return;
            stopping = true; acceptingAudio = false; inbox.ClearAudio(); ResetAcknowledgements();
            if (helper is { HasExited: false } && pipe is { IsConnected: true })
            {
                try
                {
                    await SendAsync(new CaptureControl(CaptureAction.Stop), cancellationToken).ConfigureAwait(false);
                    await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                    await helper.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                    if (helper.ExitCode != 0) throw new IOException($"Native helper exited with code {helper.ExitCode}.");
                }
                catch (Exception error) { failure = error; }
            }
        }
        finally
        {
            if (stopping)
            {
                await ReleaseHelperAsync().ConfigureAwait(false);
                inbox.Complete(clear: true);
            }
            lifecycle.Release();
        }
        if (failure is not null) throw new IOException("Native cleanup required helper termination; graceful Stop was not verified.", failure);
    }

    private void EnsureActive()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!started || stopping || helper is null || helper.HasExited) throw new InvalidOperationException("Capture session is not active.");
    }

    private void ResetAcknowledgements()
    {
        lock (acknowledgementGate)
        {
            acknowledged = NewAcknowledgement();
            stoppedSources = new HashSet<AudioStreamId>(terminalSources);
            if (stoppedSources.Contains(AudioStreamId.LocalMic) && stoppedSources.Contains(AudioStreamId.RemoteApp)) acknowledged.TrySetResult();
        }
    }

    private async Task FailControlAsync()
    {
        stopping = true; acceptingAudio = false;
        await ReleaseHelperAsync().ConfigureAwait(false); inbox.Complete(clear: true);
    }

    private void KillOwnedHelper()
    {
        try { if (helper is { HasExited: false }) helper.Kill(entireProcessTree: false); }
        catch (InvalidOperationException) { }
    }

    private async Task ReleaseHelperAsync()
    {
        acceptingAudio = false; lifetime.Cancel(); pipe?.Dispose();
        KillOwnedHelper();
        Exception? terminationFailure = null;
        if (helper is not null)
        {
            try { await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException error) { terminationFailure = error; }
            helper.Dispose(); helper = null;
        }
        if (reader is not null) await reader.ConfigureAwait(false);
        if (terminationFailure is not null) throw new IOException("Owned native helper did not terminate within the cleanup deadline.", terminationFailure);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
        finally { disposed = true; inbox.Complete(clear: true); lifetime.Dispose(); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

    private static NamedPipeServerStream CreateLocalPipe(string name, PipeSecurity security)
    {
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pinned.AddrOfPinnedObject() };
            // Duplex + overlapped + first instance. Byte mode + reject remote clients.
            var handle = CreateNamedPipe($@"\\.\pipe\{name}", 0x40080003, 0x8, 1, 65_536, 65_536, 0, ref attributes);
            if (handle.IsInvalid) { handle.Dispose(); throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { pinned.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode,
        uint maxInstances, uint outputBuffer, uint inputBuffer, uint defaultTimeout, ref SecurityAttributes attributes);
}
