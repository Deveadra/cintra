using System.Diagnostics;
using System.Globalization;
using MeetingCompanion.Audio;
using MeetingCompanion.Contracts;
using MeetingCompanion.Stt;

// Manual-only. Both capture acknowledgement and an explicit paid-API flag are required.
if (!args.Contains("--acknowledge-capture") || !args.Contains("--acknowledge-playback") ||
    !args.Contains("--approve-paid-api") || !TryArgument("--mic", out var mic) ||
    !TryArgument("--seconds", out var rawSeconds) || !int.TryParse(rawSeconds, out var seconds) ||
    seconds is < 8 or > 20 || !TryArgument("--max-cost-usd", out var rawCost) ||
    !decimal.TryParse(rawCost, NumberStyles.Number, CultureInfo.InvariantCulture, out var cap) ||
    cap is <= 0 or > 0.05m)
{
    Console.Error.WriteLine("REHEARSAL_NOT_STARTED: require --mic <fresh endpoint ID> --seconds 8..20 --max-cost-usd 0.01..0.05 --acknowledge-capture --acknowledge-playback --approve-paid-api");
    return 2;
}

const decimal publishedDollarsPerMinute = 0.017m;
var estimatedTwoStreamCost = 2m * seconds / 60m * publishedDollarsPerMinute;
if (cap < estimatedTwoStreamCost)
{
    Console.Error.WriteLine("REHEARSAL_NOT_STARTED: configured cost cap is below the two-stream duration estimate.");
    return 2;
}

var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? ReadLocalKey();
if (string.IsNullOrWhiteSpace(key))
{
    Console.Error.WriteLine("REHEARSAL_NOT_STARTED: OPENAI_API_KEY unavailable.");
    return 2;
}

var root = FindRoot();
var helper = Path.Combine(root, "artifacts", "native", "Cintra.Audio.Native.exe");
if (!File.Exists(helper))
{
    Console.Error.WriteLine("REHEARSAL_NOT_STARTED: build the accepted native helper first.");
    return 2;
}

// The SAPI voice is only a reproducible synthetic renderer. Its process may not
// carry audio on every Windows setup; listen and inspect source health before
// interpreting any remote transcript.
var voiceScript = "$voice = New-Object -ComObject SAPI.SpVoice; $voice.Volume = 20; " +
    "Start-Sleep -Seconds 5; " +
    "[void]$voice.Speak('Synthetic loopback speech for the meeting companion rehearsal.'); " +
    "Start-Sleep -Seconds 12";
var start = new ProcessStartInfo("powershell.exe")
{
    UseShellExecute = false,
    CreateNoWindow = true,
    RedirectStandardError = true
};
foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", voiceScript })
    start.ArgumentList.Add(argument);

using var renderer = Process.Start(start) ?? throw new IOException("Synthetic renderer could not start.");
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
var selection = new CaptureSelection(
    new CaptureSource(AudioStreamId.LocalMic, "Operator microphone", mic, null, false, false),
    new CaptureSource(AudioStreamId.RemoteApp, "Synthetic SAPI renderer", null, renderer.Id, true, false));
var clock = new ClockMapping(Stopwatch.GetTimestamp(), Stopwatch.Frequency, DateTimeOffset.UtcNow);
var finals = new Dictionary<AudioStreamId, int>();
var gaps = 0;
try
{
    var capture = new NativeAudioCaptureSession(helper);
    await using var transcriber = new DualStreamTranscriber(capture,
        _ => new OpenAiRealtimeSocketFactory(), () => key);
    var reading = Task.Run(async () =>
    {
        await foreach (var output in transcriber.ReadAllAsync(CancellationToken.None))
        {
            switch (output)
            {
                case TranscriptFinal final:
                    finals.TryGetValue(final.StreamId, out var count);
                    finals[final.StreamId] = count + 1;
                    Console.WriteLine($"{final.StreamId}: {final.Text}");
                    break;
                case AudioGapEvent gap:
                    gaps++;
                    Console.WriteLine($"GAP {gap.Gap.StreamId} {gap.Gap.Reason}");
                    break;
                case SessionStateEvent state when state.State is SessionState.Degraded or SessionState.Error:
                    Console.WriteLine($"HEALTH {string.Join(',', state.Health.Select(x => $"{x.Component}:{x.Status}:{x.DiagnosticCode}"))}");
                    break;
            }
        }
    });
    try { await transcriber.RunAsync(selection, clock, deadline.Token); }
    catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
    await reading.WaitAsync(TimeSpan.FromSeconds(5));
}
finally
{
    if (!renderer.HasExited)
    {
        renderer.Kill(false);
        await renderer.WaitForExitAsync();
    }
}
Console.WriteLine($"REHEARSAL_ENDED mic_finals={finals.GetValueOrDefault(AudioStreamId.LocalMic)} remote_finals={finals.GetValueOrDefault(AudioStreamId.RemoteApp)} gaps={gaps}; no compatibility claim");
return 0;

bool TryArgument(string name, out string value)
{
    var index = Array.IndexOf(args, name);
    value = index >= 0 && index + 1 < args.Length ? args[index + 1] : "";
    return value.Length > 0;
}

string? ReadLocalKey()
{
    var file = Path.Combine(FindRoot(), ".env.local");
    if (!File.Exists(file)) return null;
    foreach (var line in File.ReadLines(file))
        if (line.StartsWith("OPENAI_API_KEY=", StringComparison.Ordinal))
            return line["OPENAI_API_KEY=".Length..].Trim();
    return null;
}

string FindRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MeetingCompanion.slnx")))
        directory = directory.Parent;
    return directory?.FullName ?? throw new IOException("Repository root missing.");
}
