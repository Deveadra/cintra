using System.Text.Json;
using MeetingCompanion.Platform;

// Read-only, no app launch, window text, capture, recording or network. Output omits account paths/session strings.
if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
{
    Console.Error.WriteLine("Windows x64 is required.");
    return 2;
}
if (args.Length != 0 && !(args.Length == 2 && args[0] == "--select-pid"))
{
    Console.Error.WriteLine("Usage: Cintra.Platform.Inventory [--select-pid <PID|self>]");
    return 2;
}
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
void Emit(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
string? Version(ProcessIdentity? identity)
{
    if (identity is null) return null;
    try { return System.Diagnostics.FileVersionInfo.GetVersionInfo(identity.ExecutablePath).FileVersion; }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
    { return "unavailable"; }
}
try
{
    var inventory = new WindowsCallSourceInventory();
    var snapshot = await inventory.ReadAsync(cancellation.Token);
    Emit(new
    {
        kind = "environment",
        snapshot.WindowsBuild,
        x64 = Environment.Is64BitProcess,
        snapshot.ProcessLoopbackSupported,
        snapshot.ProcessesComplete,
        snapshot.Diagnostics,
        processCount = snapshot.Processes.Length,
        identifiedProcessCount = snapshot.Processes.Count(p => p.Identity is not null),
        endpoints = snapshot.Endpoints.Select(e => new { e.DisplayName, e.IsCapture }),
        sessionCount = snapshot.Sessions.Length,
        activeSessions = snapshot.Sessions.Count(s => s.State == RenderSessionState.Active),
        compatibility = "Zoom/Teams meetings and direct calls NOT VERIFIED"
    });
    foreach (var kind in new[] { CallSourceKind.Zoom, CallSourceKind.Teams })
    {
        var adapter = new WindowsCallSourceAdapter(inventory, kind);
        var candidates = await adapter.DiscoverAsync(cancellation.Token);
        Emit(new { kind = "candidate_count", adapter = adapter.Id, count = candidates.Count });
        foreach (var c in candidates)
        {
            var status = await adapter.ValidateAsync(c, cancellation.Token);
            Emit(new
            {
                kind = "candidate",
                adapter = adapter.Id,
                c.ProcessId,
                c.ProcessTree,
                c.AppDisplayName,
                executable = Path.GetFileName(c.ExecutablePath),
                c.RenderActive,
                audioSessionCount = c.AudioSessionIds.Length,
                c.SelectionHint,
                validity = status.Validity.ToString(),
                status.DiagnosticCode,
                status.RendererProcessIds
            });
            Emit(new
            {
                kind = "process_tree_evidence",
                c.ProcessId,
                members = snapshot.Processes.Where(p => c.ProcessTree.Contains(p.ProcessId)).Select(p => new
                {
                    p.ProcessId,
                    p.ParentProcessId,
                    p.ExecutableName,
                    creationFileTime = p.Identity?.CreationFileTime,
                    p.DiagnosticCode,
                    version = Version(p.Identity)
                }),
                sessions = snapshot.Sessions.Where(s => s.Process is not null && c.ProcessTree.Contains(s.Process.ProcessId))
                    .Select(s => new { s.Process!.ProcessId, state = s.State.ToString(), s.MultiProcess, s.SystemSounds })
            });
        }
    }
    var manual = new WindowsCallSourceAdapter(inventory, CallSourceKind.Manual);
    var offered = await manual.DiscoverAsync(cancellation.Token);
    int pid = args.Length == 0 || args[1] == "self" ? Environment.ProcessId : int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
    var selected = offered.SingleOrDefault(c => c.ProcessId == pid);
    if (selected is null) { Emit(new { kind = "selection", pid, result = "process_not_available" }); return 2; }
    var probe = await manual.ProbeAsync(selected, cancellation.Token);
    Emit(new
    {
        kind = "manual_probe",
        pid,
        selected.ProcessTree,
        executable = Path.GetFileName(selected.ExecutablePath),
        outcome = probe.Outcome.ToString(),
        probe.DiagnosticCode
    });
    using var lease = await manual.AcquireAsync(selected, cancellation.Token);
    lease.EnsureAlive();
    Emit(new { kind = "manual_selection", pid, result = "identity_pinned_no_capture", stream = lease.Resolved.Source.StreamId.ToString() });
    return snapshot.AudioInventoryComplete && snapshot.ProcessesComplete ? 0 : 2;
}
catch (Exception e) when (e is CallSourceSelectionException or OperationCanceledException or FormatException or OverflowException)
{
    Emit(new { kind = "error", code = e is CallSourceSelectionException s ? s.DiagnosticCode : e.GetType().Name });
    return 2;
}
