using System.Diagnostics;
using System.Text.Json;
using MeetingCompanion.Audio;
using MeetingCompanion.Contracts;

// Explicit bounded, in-memory native validation. Prints only health/timing/meter metadata.
if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: helper_absolute_path --inventory | helper_absolute_path --capture --mic endpoint_id --pid process_id --seconds 1..30 --acknowledge-capture");
    return 2;
}
var helperPath = Path.GetFullPath(args[0]);
if (args[1] == "--inventory")
{
    using var inventory = Process.Start(new ProcessStartInfo(helperPath) { UseShellExecute = false, ArgumentList = { "--inventory" } })!;
    await inventory.WaitForExitAsync(); return inventory.ExitCode;
}
string Option(string name)
{
    var position = Array.IndexOf(args, name);
    return position >= 0 && position + 1 < args.Length ? args[position + 1] : throw new ArgumentException($"Required {name}.");
}
if (args[1] != "--capture" || !args.Contains("--acknowledge-capture")) throw new ArgumentException("Explicit capture acknowledgement required.");
var pid = Option("--pid") == "self" ? Environment.ProcessId : int.Parse(Option("--pid"), System.Globalization.CultureInfo.InvariantCulture);
var seconds = int.Parse(Option("--seconds"), System.Globalization.CultureInfo.InvariantCulture);
if (seconds is < 1 or > 30) throw new ArgumentException("Duration must be 1..30 seconds.");
var device = Option("--mic");
var selection = new CaptureSelection(new(AudioStreamId.LocalMic, "Selected microphone", device, null, false, false), new(AudioStreamId.RemoteApp, "Selected process tree", null, pid, true, false));
Console.WriteLine(JsonSerializer.Serialize(new { mode = "real_windows_native", pid, device, seconds, recording = false }));
using var duration = new CancellationTokenSource(TimeSpan.FromSeconds(8));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; duration.Cancel(); };
await using var session = new NativeAudioCaptureSession(helperPath);
await session.StartAsync(selection, new ClockMapping(Stopwatch.GetTimestamp(), Stopwatch.Frequency, DateTimeOffset.UtcNow), CancellationToken.None);
var counts = new Dictionary<AudioStreamId, int>();
var peaks = new Dictionary<AudioStreamId, double>();
var failure = false;
var readySources = new HashSet<AudioStreamId>();
var sourceReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var controlTask = args.Contains("--exercise-controls") ? Task.Run(async () =>
{
    await sourceReady.Task.WaitAsync(duration.Token);
    await Task.Delay(300, duration.Token);
    await session.PauseAsync(duration.Token);
    Console.WriteLine("{\"control\":\"pause_acknowledged\"}");
    await Task.Delay(250, duration.Token);
    await session.ResumeAsync(duration.Token);
    Console.WriteLine("{\"control\":\"resume_sent\"}");
}) : Task.CompletedTask;
try
{
    await foreach (var message in session.ReadAllAsync(duration.Token))
    {
        if (message is CaptureSourceChanged changed)
        {
            readySources.Add(changed.Source.StreamId);
            if (readySources.Count == 2 && !sourceReady.Task.IsCompleted) { sourceReady.SetResult(); duration.CancelAfter(TimeSpan.FromSeconds(seconds)); }
        }
        if (message is AudioFrame frame)
        {
            counts.TryGetValue(frame.StreamId, out var count); counts[frame.StreamId] = count + 1;
            double peak = 0;
            for (int i = 0; i < frame.Pcm.Length; i += 2) peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(frame.Pcm, i) / 32768d));
            peaks.TryGetValue(frame.StreamId, out var oldPeak); peaks[frame.StreamId] = Math.Max(oldPeak, peak);
        }
        else
        {
            Console.WriteLine(JsonSerializer.Serialize(message, ContractJson.Options));
            if (message is CaptureHealth { Health.Status: HealthStatus.Failed or HealthStatus.WrongProcess or HealthStatus.NoDevice or HealthStatus.PermissionDenied or HealthStatus.Disconnected }) failure = true;
        }
    }
}
catch (OperationCanceledException) when (duration.IsCancellationRequested) { }
try { await controlTask; } catch (OperationCanceledException) when (duration.IsCancellationRequested) { failure = true; }
if (readySources.Count != 2) failure = true;
await session.StopAsync(CancellationToken.None);
Console.WriteLine(JsonSerializer.Serialize(new { result = failure ? "FAIL" : "CAPTURE_ENDED", frames = counts.ToDictionary(x => x.Key.ToString(), x => x.Value), peaks = peaks.ToDictionary(x => x.Key.ToString(), x => x.Value) }));
return failure ? 2 : 0;
