using System.Security.Cryptography;
using System.Text;
using MeetingCompanion.Contracts;

namespace MeetingCompanion.Platform;

public enum CallSourceKind { Zoom, Teams, Manual }
public enum SourceValidity { Ready, Unsupported, InventoryUnavailable, IdentityUnavailable, SourceLost, SourceChanged, Ambiguous }
public sealed record CallSourceStatus(SourceValidity Validity, string DiagnosticCode, bool RenderActive,
    int[] RendererProcessIds)
{
    public bool CanSelect => Validity == SourceValidity.Ready;
}
public sealed class CallSourceSelectionException(string diagnosticCode) : InvalidOperationException(diagnosticCode)
{
    public string DiagnosticCode { get; } = diagnosticCode;
}

/// <summary>Operator-selected source and held process identity. This is not capture health or call verification.</summary>
public sealed class SelectedCallSource : IDisposable
{
    private readonly IProcessIdentityLease lease;
    internal SelectedCallSource(ResolvedCallSource resolved, IProcessIdentityLease lease)
    {
        Resolved = resolved;
        this.lease = lease;
    }
    public ResolvedCallSource Resolved { get; }
    public bool IsAlive => lease.IsAlive;
    public void EnsureAlive()
    {
        if (!IsAlive) throw new CallSourceSelectionException("selected_process_exited");
    }
    public void Dispose() => lease.Dispose();
}

/// <summary>
/// Calendar-independent discovery for meetings and direct calls. Manual mode presents every observed process.
/// Removed/changed candidates expire on rediscovery; a changed root/tree/session requires a fresh operator choice.
/// </summary>
public sealed class WindowsCallSourceAdapter(ICallSourceInventory inventory, CallSourceKind kind) : ICallSourceAdapter
{
    private readonly object gate = new();
    private Dictionary<string, Entry> offered = new(StringComparer.Ordinal);
    private sealed record Entry(CallAudioCandidate Candidate, ProcessIdentity? Identity, string Fingerprint,
        CallSourceStatus Status);
    public string Id => kind switch { CallSourceKind.Zoom => "windows_zoom", CallSourceKind.Teams => "windows_teams", _ => "windows_manual" };

    public async Task<IReadOnlyList<CallAudioCandidate>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var snapshot = await inventory.ReadAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var entries = Build(snapshot);
        lock (gate) offered = entries.ToDictionary(e => e.Candidate.Id, StringComparer.Ordinal);
        return entries.Select(e => Copy(e.Candidate)).ToArray();
    }

    public async Task<CallSourceStatus> ValidateAsync(CallAudioCandidate candidate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(candidate);
        Entry? previous;
        lock (gate) offered.TryGetValue(candidate.Id, out previous);
        if (previous is null || !SameCandidate(candidate, previous.Candidate))
            return Status(SourceValidity.SourceChanged, "candidate_not_offered");
        var snapshot = await inventory.ReadAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var process = snapshot.Processes.SingleOrDefault(p => p.ProcessId == previous.Candidate.ProcessId);
        if (!snapshot.ProcessesComplete) return Status(SourceValidity.InventoryUnavailable, "process_inventory_incomplete");
        if (process is null) return Status(SourceValidity.SourceLost, "selected_process_exited");
        if (process.Identity != previous.Identity)
            return Status(SourceValidity.SourceChanged, "process_identity_changed");
        var current = Build(snapshot).SingleOrDefault(e => e.Candidate.ProcessId == candidate.ProcessId);
        if (current is null) return Status(SourceValidity.SourceChanged, "process_tree_changed");
        if (!current.Status.CanSelect) return current.Status;
        if (current.Fingerprint != previous.Fingerprint)
            return Status(SourceValidity.SourceChanged, "renderer_or_tree_changed");
        return current.Status;
    }

    public async Task<SourceProbeResult> ProbeAsync(CallAudioCandidate candidate, CancellationToken cancellationToken)
    {
        var status = await ValidateAsync(candidate, cancellationToken).ConfigureAwait(false);
        // GetState(Active) means a stream is running, not that audible call audio was detected.
        var outcome = status.Validity switch
        {
            SourceValidity.Ready => ProbeOutcome.Silence,
            SourceValidity.InventoryUnavailable => ProbeOutcome.DeviceUnavailable,
            SourceValidity.Ambiguous or SourceValidity.SourceChanged => ProbeOutcome.Ambiguous,
            _ => ProbeOutcome.NotSupported
        };
        return new(outcome, status.CanSelect
            ? status.RenderActive ? "render_stream_active_audio_unverified" : "no_active_render_stream_audio_unverified"
            : status.DiagnosticCode);
    }

    public async Task<ResolvedCallSource> ResolveAsync(CallAudioCandidate candidate, CancellationToken cancellationToken)
    {
        var status = await ValidateAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (!status.CanSelect) throw new CallSourceSelectionException(status.DiagnosticCode);
        return Resolve(candidate);
    }

    /// <summary>
    /// Preferred capture boundary: revalidate the offered choice, pin its creation identity, then start the native helper.
    /// Caller serializes selection/start/stop, holds the lease through Stop, and periodically calls ValidateAsync.
    /// </summary>
    public async Task<SelectedCallSource> AcquireAsync(CallAudioCandidate candidate, CancellationToken cancellationToken)
    {
        var status = await ValidateAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (!status.CanSelect) throw new CallSourceSelectionException(status.DiagnosticCode);
        Entry entry;
        lock (gate)
        {
            if (!offered.TryGetValue(candidate.Id, out entry!) || !SameCandidate(candidate, entry.Candidate))
                throw new CallSourceSelectionException("candidate_not_offered");
        }
        var lease = inventory.Pin(entry.Identity!);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!lease.IsAlive) throw new CallSourceSelectionException("selected_process_exited");
            return new(Resolve(entry.Candidate), lease);
        }
        catch { lease.Dispose(); throw; }
    }

    private static ResolvedCallSource Resolve(CallAudioCandidate candidate) => new(
        new CaptureSource(AudioStreamId.RemoteApp, candidate.AppDisplayName, null, candidate.ProcessId, true, false),
        "Keep the process identity lease through Stop. On source loss or renderer/tree change: stop, mark a gap, rediscover and ask for a fresh choice. Never follow a numeric PID.");
    private static CallSourceStatus Status(SourceValidity validity, string code) => new(validity, code, false, []);
    private static CallAudioCandidate Copy(CallAudioCandidate c) => c with { ProcessTree = [.. c.ProcessTree], AudioSessionIds = [.. c.AudioSessionIds] };
    private static bool SameCandidate(CallAudioCandidate a, CallAudioCandidate b) => a == b ||
        (a with { ProcessTree = b.ProcessTree, AudioSessionIds = b.AudioSessionIds }) == b &&
        a.ProcessTree.SequenceEqual(b.ProcessTree) && a.AudioSessionIds.SequenceEqual(b.AudioSessionIds);

    private bool Matches(ProcessObservation p) => kind switch
    {
        CallSourceKind.Zoom => p.ExecutableName.Equals("Zoom.exe", StringComparison.OrdinalIgnoreCase),
        CallSourceKind.Teams => p.ExecutableName.Equals("ms-teams.exe", StringComparison.OrdinalIgnoreCase) ||
            p.ExecutableName.Equals("Teams.exe", StringComparison.OrdinalIgnoreCase),
        _ => p.ProcessId > 0
    };
    private List<Entry> Build(CallSourceInventory snapshot)
    {
        var processes = snapshot.Processes.ToDictionary(p => p.ProcessId);
        bool DescendsFrom(ProcessObservation child, ProcessObservation parent) =>
            child.ParentProcessId == parent.ProcessId && child.Identity is { } ci &&
            parent.Identity is { } pi && ci.CreationFileTime >= pi.CreationFileTime &&
            child.ProcessId != parent.ProcessId;
        bool HasMatchingAncestor(ProcessObservation p)
        {
            var seen = new HashSet<int> { p.ProcessId };
            while (processes.TryGetValue(p.ParentProcessId, out var parent) && DescendsFrom(p, parent) && seen.Add(parent.ProcessId))
            {
                if (Matches(parent)) return true;
                p = parent;
            }
            return false;
        }
        List<Entry> entries = [];
        foreach (var root in snapshot.Processes.Where(Matches).OrderBy(p => p.ProcessId))
        {
            if (kind != CallSourceKind.Manual && HasMatchingAncestor(root)) continue;
            var tree = new Dictionary<int, ProcessObservation> { [root.ProcessId] = root };
            bool added;
            do
            {
                added = false;
                foreach (var p in snapshot.Processes)
                    if (!tree.ContainsKey(p.ProcessId) && tree.TryGetValue(p.ParentProcessId, out var parent) && DescendsFrom(p, parent))
                    { tree.Add(p.ProcessId, p); added = true; }
            } while (added);
            var uncertainChild = snapshot.Processes.Any(p => tree.ContainsKey(p.ParentProcessId) && p.Identity is null && p.ProcessId != root.ProcessId);
            var sessions = snapshot.Sessions.Where(s => s.Process is { } identity &&
                tree.TryGetValue(identity.ProcessId, out var p) && p.Identity == identity && s.State != RenderSessionState.Expired).ToArray();
            var renderers = sessions.Where(s => s.State == RenderSessionState.Active && !s.SystemSounds)
                .Select(s => s.Process!.ProcessId).Distinct().Order().ToArray();
            var validity = !snapshot.ProcessLoopbackSupported ? SourceValidity.Unsupported :
                root.Identity is null ? SourceValidity.IdentityUnavailable :
                !snapshot.AudioInventoryComplete || !snapshot.Endpoints.Any(e => !e.IsCapture) ? SourceValidity.InventoryUnavailable :
                uncertainChild ? SourceValidity.IdentityUnavailable :
                sessions.Any(s => s.SystemSounds) || snapshot.Sessions.Any(s => s.MultiProcess && !s.SystemSounds && s.State != RenderSessionState.Expired)
                    ? SourceValidity.Ambiguous : SourceValidity.Ready;
            var code = validity switch
            {
                SourceValidity.Unsupported => "process_loopback_unsupported",
                SourceValidity.IdentityUnavailable => "process_identity_unavailable",
                SourceValidity.InventoryUnavailable => "audio_inventory_incomplete",
                SourceValidity.Ambiguous => "shared_audio_session",
                _ => "process_tree_valid_audio_unverified"
            };
            var active = renderers.Length > 0;
            var fingerprint = string.Join("|", tree.Values.OrderBy(p => p.ProcessId).Select(p => p.Identity?.ToString() ?? $"unknown:{p.ProcessId}"))
                + "#" + string.Join("|", sessions.OrderBy(s => s.EndpointId).ThenBy(s => s.SessionId)
                    .Select(s => $"{s.EndpointId}:{s.SessionId}:{s.Process}:{s.MultiProcess}:{s.SystemSounds}"));
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)));
            var name = kind switch { CallSourceKind.Zoom => "Zoom", CallSourceKind.Teams => "Microsoft Teams", _ => root.ExecutableName };
            var hint = kind == CallSourceKind.Manual && IsBrowser(root.ExecutableName)
                ? "Browser audio: other tabs and child processes may be included. Verify what you hear before use."
                : "Includes all audio in this app process tree, including notifications. Meetings and direct calls use the same source. Call audio is unverified.";
            var c = new CallAudioCandidate($"{Id}:{digest}", name, root.ProcessId, tree.Keys.Order().ToArray(),
                root.Identity?.ExecutablePath ?? "", sessions.Select(s => s.EndpointId + "/" + s.SessionId).Order().ToArray(),
                active, root.WindowHandle, active ? Confidence.Moderate : Confidence.Low, hint);
            entries.Add(new(c, root.Identity, fingerprint, new(validity, code, active, renderers)));
        }
        return entries;
    }
    private static bool IsBrowser(string name) => new[] { "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe" }
        .Contains(name, StringComparer.OrdinalIgnoreCase);
}
