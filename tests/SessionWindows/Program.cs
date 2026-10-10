using System.Diagnostics;
using MeetingCompanion.Contracts;
using MeetingCompanion.Platform;
using MeetingCompanion.Session;

// Manual, no provider network access. Inventory alone never captures; explicit acknowledgments are mandatory.
if (!args.Contains("--acknowledge-capture") || !args.Contains("--acknowledge-playback") ||
    !Argument("--mic", out var mic) || !Argument("--pid", out var rawPid) || !int.TryParse(rawPid, out int pid) || pid <= 0)
{
    Console.Error.WriteLine("SMOKE_NOT_STARTED: --mic <fresh endpoint ID> --pid <operator-selected synthetic tone PID> --acknowledge-capture --acknowledge-playback required. No paid provider.");
    return 2;
}
var root = FindRoot();
var helper = Path.Combine(root, "artifacts", "native", "Cintra.Audio.Native.exe");
if (!File.Exists(helper)) { Console.Error.WriteLine("SMOKE_NOT_STARTED: run scripts/native-build.ps1 first."); return 2; }
var inventory = new WindowsCallSourceInventory();
await using var coordinator = new MeetingCoordinator(inventory,
    new MeetingRuntimeFactory(helper, () => false, () => throw new InvalidOperationException("Smoke must never read credentials.")));
var sources = await coordinator.DiscoverAsync(CallSourceKind.Manual, default);
var candidate = sources.Applications.SingleOrDefault(c => c.ProcessId == pid);
var microphone = sources.Microphones.SingleOrDefault(m => m.Id == mic);
if (candidate is null || microphone is null) { Console.Error.WriteLine("SMOKE_NOT_STARTED: source or microphone absent from fresh inventory."); return 2; }
try
{
    var choice = new MeetingChoice(CallSourceKind.Manual, candidate, microphone);
    await coordinator.StartAsync(choice, ProviderMode.OfflineTest, default);
    await ObserveFor(TimeSpan.FromSeconds(4));
    var meeting = coordinator.View.SessionId;
    await coordinator.PauseAsync(default);
    if (coordinator.View.State != SessionState.Paused) throw new InvalidOperationException("Pause failed.");
    Console.WriteLine("PAUSE_OK: capture and both offline transports released.");
    await coordinator.ResumeAsync(default);
    if (coordinator.View.SessionId != meeting) throw new InvalidOperationException("Resume changed the meeting timeline.");
    await ObserveFor(TimeSpan.FromSeconds(4));
    await coordinator.StopAsync();
    if (coordinator.View.SessionId is not null || coordinator.View.Transcripts.Length > 0 || coordinator.LatestSnapshot is not null)
        throw new InvalidOperationException("Stop retained meeting data.");
    Console.WriteLine("STOP_OK: coordinator reports resources released and memory cleared. This is synthetic process-tree evidence only.");
    return 0;
}
catch
{
    Console.Error.WriteLine("SMOKE_FAILED: inspect source identity, levels and coordinator health. No real-call compatibility claim.");
    return 1;
}

async Task ObserveFor(TimeSpan duration)
{
    var timer = Stopwatch.StartNew();
    while (timer.Elapsed < duration)
    {
        await coordinator.PollAsync(default);
        var view = coordinator.View;
        Console.WriteLine($"TEST MODE {view.State}: {string.Join(';', view.Health.Select(h => $"{h.Component}={h.Status}:{h.DiagnosticCode}"))} peaks={view.LocalPeak:F3}/{view.RemotePeak:F3} scripted-turns={view.Transcripts.Length}");
        if (view.State == SessionState.Error) throw new InvalidOperationException("Coordinator failed.");
        await Task.Delay(500);
    }
}
bool Argument(string name, out string value)
{
    int index = Array.IndexOf(args, name);
    value = index >= 0 && index + 1 < args.Length ? args[index + 1] : "";
    return value.Length > 0;
}
string FindRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MeetingCompanion.slnx"))) directory = directory.Parent;
    return directory?.FullName ?? throw new InvalidOperationException("Repository root unavailable.");
}
