using System.IO.Pipes;
using MeetingCompanion.Contracts;

if (args.Length != 3 || args[0] != "--pipe") return 2;
using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
await pipe.ConnectAsync(2000);
var start = await IpcFraming.ReadAsync(pipe, TimeSpan.FromSeconds(2), CancellationToken.None) ?? throw new IOException("Missing start.");
var mode = Environment.GetEnvironmentVariable("CINTRA_FAKE_HELPER_MODE");
var mapping = ((StartCapture)start.Payload).Clock;
long Now() => Math.Max(0, (long)((System.Diagnostics.Stopwatch.GetTimestamp() - mapping.MonotonicOriginTicks) * 1000d / mapping.TicksPerSecond));
if (mode == "crash") return 3;
if (mode == "stall") { await Task.Delay(10_000); return 4; }
var session = mode == "wrong-session" ? Guid.NewGuid() : start.SessionId;
async Task Send(CaptureMessage message) => await IpcFraming.WriteAsync(pipe, new IpcEnvelope(1, Guid.NewGuid(), session, message), TimeSpan.FromSeconds(2), CancellationToken.None);
async Task Health(HealthStatus status)
{
    foreach (var stream in new[] { AudioStreamId.LocalMic, AudioStreamId.RemoteApp })
        await Send(new CaptureHealth(stream, new ComponentHealth(stream == AudioStreamId.LocalMic ? Component.LocalCapture : Component.RemoteCapture, status, Now(), "fake_offline"), .1, 0, 0));
}
long sequence = 0;
async Task Frames()
{
    foreach (var stream in new[] { AudioStreamId.LocalMic, AudioStreamId.RemoteApp })
        await Send(new AudioFrame(stream, sequence, 0, 20, AudioFormat.Normalized, 48000, new byte[960]));
    sequence++;
}
await Health(HealthStatus.Healthy);
await Frames();
if (mode == "remote-stall")
{
    for (int i = 0; i < 30; ++i)
    {
        await Send(new CaptureHealth(AudioStreamId.LocalMic, new ComponentHealth(Component.LocalCapture, HealthStatus.Healthy, Now(), "fake_mic_alive_remote_stalled"), .1, 0, 0));
        await Task.Delay(200);
    }
    return 6;
}
while (true)
{
    var command = await IpcFraming.ReadAsync(pipe, TimeSpan.FromSeconds(10), CancellationToken.None);
    if (command?.Payload is not CaptureControl control) return 2;
    if (mode == "ignore-stop") { await Task.Delay(10_000); return 5; }
    if (control.Action == CaptureAction.Stop) { await Health(HealthStatus.Stopped); return 0; }
    if (control.Action == CaptureAction.Pause) await Health(HealthStatus.Stopped);
    if (control.Action == CaptureAction.Resume) { await Health(HealthStatus.Healthy); await Frames(); }
}
