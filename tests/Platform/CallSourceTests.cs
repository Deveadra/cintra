using MeetingCompanion.Contracts;
using MeetingCompanion.Platform;

namespace MeetingCompanion.Platform.Tests;

public class CallSourceTests
{
    private static ProcessObservation P(int pid, int parent = 0, string name = "Zoom.exe", long born = 100) =>
        new(pid, parent, name, new(pid, born, "C:\\Apps\\" + name), null);
    private static RenderSession S(ProcessObservation p, string id = "session", bool active = true, bool multi = false) =>
        new("output", id, p.Identity, active ? RenderSessionState.Active : RenderSessionState.Inactive, multi, false);
    private static CallSourceInventory Snapshot(ProcessObservation[] processes, params RenderSession[] sessions) =>
        new(26200, true, processes, [new("output", "Speakers", false)], sessions, []);
    private sealed class Fixture(CallSourceInventory snapshot) : ICallSourceInventory
    {
        public CallSourceInventory Current = snapshot;
        public bool RejectPin;
        public readonly FakeLease Lease = new();
        public int Pins;
        public Task<CallSourceInventory> ReadAsync(CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(Current); }
        public IProcessIdentityLease Pin(ProcessIdentity identity)
        {
            Pins++;
            if (RejectPin || !Current.Processes.Any(p => p.Identity == identity))
                throw new CallSourceSelectionException("process_identity_changed");
            return Lease;
        }
    }
    private sealed class FakeLease : IProcessIdentityLease
    {
        public bool Alive = true;
        public bool Disposed;
        public bool IsAlive => Alive && !Disposed;
        public void Dispose() => Disposed = true;
    }
    private static async Task<CallAudioCandidate> One(WindowsCallSourceAdapter adapter) =>
        Assert.Single(await adapter.DiscoverAsync(default));

    [Theory]
    [InlineData(CallSourceKind.Zoom, "Zoom.exe")]
    [InlineData(CallSourceKind.Teams, "ms-teams.exe")]
    [InlineData(CallSourceKind.Teams, "Teams.exe")]
    public async Task MeetingAndDirectCallUseObservedRendererTreeWithoutCalendar(CallSourceKind kind, string name)
    {
        var root = P(10, name: name);
        var renderer = P(11, 10, "msedgewebview2.exe", 110);
        var other = P(99, name: "music.exe");
        var f = new Fixture(Snapshot([root, renderer, other], S(renderer), S(other, "music")));
        var a = new WindowsCallSourceAdapter(f, kind);
        var c = await One(a);
        Assert.Equal(10, c.ProcessId);
        Assert.Equal(new[] { 10, 11 }, c.ProcessTree);
        Assert.Single(c.AudioSessionIds);
        Assert.True(c.RenderActive);
        Assert.Equal(new[] { 11 }, (await a.ValidateAsync(c, default)).RendererProcessIds);
        Assert.Equal(ProbeOutcome.Silence, (await a.ProbeAsync(c, default)).Outcome);
        Assert.Equal("render_stream_active_audio_unverified", (await a.ProbeAsync(c, default)).DiagnosticCode);
        var resolved = await a.ResolveAsync(c, default);
        Assert.Equal(AudioStreamId.RemoteApp, resolved.Source.StreamId);
        Assert.True(resolved.Source.IncludeProcessTree);
        resolved.Source.Validate();
    }

    [Fact]
    public async Task StartingAndStoppingAppChangesDiscoveryAndInvalidatesChoice()
    {
        var f = new Fixture(Snapshot([]));
        var a = new WindowsCallSourceAdapter(f, CallSourceKind.Zoom);
        Assert.Empty(await a.DiscoverAsync(default));
        f.Current = Snapshot([P(10)]);
        var c = await One(a);
        f.Current = Snapshot([]);
        Assert.Equal(SourceValidity.SourceLost, (await a.ValidateAsync(c, default)).Validity);
        var e = await Assert.ThrowsAsync<CallSourceSelectionException>(() => a.AcquireAsync(c, default));
        Assert.Equal("selected_process_exited", e.DiagnosticCode);
        Assert.Equal(0, f.Pins);
        Assert.Empty(await a.DiscoverAsync(default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PidReuseNeverFollowsSameNameOrDifferentApp(bool sameName)
    {
        var f = new Fixture(Snapshot([P(10)]));
        var a = new WindowsCallSourceAdapter(f, CallSourceKind.Zoom);
        var old = await One(a);
        f.Current = Snapshot([P(10, name: sameName ? "Zoom.exe" : "music.exe", born: 999)]);
        Assert.Equal("process_identity_changed", (await a.ValidateAsync(old, default)).DiagnosticCode);
        await Assert.ThrowsAsync<CallSourceSelectionException>(() => a.ResolveAsync(old, default));
        if (sameName) Assert.NotEqual(old.Id, (await One(a)).Id);
        else Assert.Empty(await a.DiscoverAsync(default));
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("stop")]
    [InlineData("start")]
    [InlineData("reuse")]
    [InlineData("session")]
    public async Task RendererChangesRequireFreshSelection(string change)
    {
        var root = P(10);
        var renderer = P(11, 10, "renderer.exe", 110);
        var f = new Fixture(Snapshot([root, renderer], S(renderer)));
        var a = new WindowsCallSourceAdapter(f, CallSourceKind.Zoom);
        var old = await One(a);
        f.Current = change switch
        {
            "replace" => Snapshot([root, P(12, 10, "renderer.exe", 120)], S(P(12, 10, "renderer.exe", 120))),
            "stop" => Snapshot([root]),
            "start" => Snapshot([root, renderer, P(12, 10, "renderer.exe", 120)], S(renderer)),
            "reuse" => Snapshot([root, P(11, 10, "renderer.exe", 999)], S(P(11, 10, "renderer.exe", 999))),
            _ => Snapshot([root, renderer], S(renderer, "new-session"))
        };
        Assert.Equal("renderer_or_tree_changed", (await a.ValidateAsync(old, default)).DiagnosticCode);
        await Assert.ThrowsAsync<CallSourceSelectionException>(() => a.AcquireAsync(old, default));
        var fresh = await One(a);
        Assert.True((await a.ValidateAsync(fresh, default)).CanSelect);
    }

    [Fact]
    public async Task HoldResumeChangesActivityWithoutPretendingAudioDetection()
    {
        var root = P(10);
        var f = new Fixture(Snapshot([root], S(root)));
        var a = new WindowsCallSourceAdapter(f, CallSourceKind.Zoom);
        var c = await One(a);
        f.Current = Snapshot([root], S(root, active: false));
        Assert.True((await a.ValidateAsync(c, default)).CanSelect);
        Assert.False((await a.ValidateAsync(c, default)).RenderActive);
        Assert.Equal("no_active_render_stream_audio_unverified", (await a.ProbeAsync(c, default)).DiagnosticCode);
        f.Current = Snapshot([root], S(root));
        Assert.True((await a.ValidateAsync(c, default)).RenderActive);
    }

    [Fact]
    public async Task MultipleAppRootsAreSeparateExplicitChoices()
    {
        var f = new Fixture(Snapshot([P(10), P(20)], S(P(10)), S(P(20), "second")));
        var a = new WindowsCallSourceAdapter(f, CallSourceKind.Zoom);
        var cs = await a.DiscoverAsync(default);
        Assert.Equal(2, cs.Count);
        Assert.NotEqual(cs[0].Id, cs[1].Id);
        Assert.Equal(20, (await a.ResolveAsync(cs[1], default)).Source.ProcessId);
    }

    [Fact]
    public async Task NestedAppProcessIsGroupedUnderLiveAncestor()
    {
        var f = new Fixture(Snapshot([P(10), P(11, 10, born: 110), P(12, 11, "renderer.exe", 120)]));
        var c = await One(new(f, CallSourceKind.Zoom));
        Assert.Equal(new[] { 10, 11, 12 }, c.ProcessTree);
    }

    [Fact]
    public async Task ReusedParentPidDoesNotAdoptOlderOrphan()
    {
        var f = new Fixture(Snapshot([P(10, born: 999), P(11, 10, "renderer.exe", 100)], S(P(11, 10, "renderer.exe", 100))));
        var c = await One(new(f, CallSourceKind.Zoom));
        Assert.Equal(new[] { 10 }, c.ProcessTree);
        Assert.False(c.RenderActive);
    }

    [Fact]
    public async Task UnknownChildIdentityBlocksSelection()
    {
        var f = new Fixture(Snapshot([P(10), P(11, 10, "renderer.exe") with { Identity = null }]));
        var a = new WindowsCallSourceAdapter(f, CallSourceKind.Zoom);
        Assert.Equal(SourceValidity.IdentityUnavailable, (await a.ValidateAsync(await One(a), default)).Validity);
    }

    [Fact]
    public async Task StaleSessionPidCannotAttributeAudioToReplacement()
    {
        var f = new Fixture(Snapshot([P(10, born: 999)], S(P(10, born: 100))));
        Assert.False((await One(new(f, CallSourceKind.Zoom))).RenderActive);
    }

    [Fact]
    public async Task SharedSessionIsExplicitlyAmbiguous()
    {
        var root = P(10);
        var f = new Fixture(Snapshot([root], S(root, multi: true)));
        var a = new WindowsCallSourceAdapter(f, CallSourceKind.Zoom);
        var c = await One(a);
        Assert.Equal(ProbeOutcome.Ambiguous, (await a.ProbeAsync(c, default)).Outcome);
        Assert.Equal("shared_audio_session", (await a.ValidateAsync(c, default)).DiagnosticCode);
        await Assert.ThrowsAsync<CallSourceSelectionException>(() => a.ResolveAsync(c, default));
    }

    [Theory]
    [InlineData("unsupported", SourceValidity.Unsupported)]
    [InlineData("permission", SourceValidity.InventoryUnavailable)]
    [InlineData("no_device", SourceValidity.InventoryUnavailable)]
    [InlineData("metadata", SourceValidity.IdentityUnavailable)]
    public async Task UnsupportedAndUnavailableAreSpecific(string fault, SourceValidity expected)
    {
        var root = P(10);
        var snap = Snapshot([root]);
        var f = new Fixture(fault switch
        {
            "unsupported" => snap with { ProcessLoopbackSupported = false },
            "permission" => snap with { Diagnostics = ["audio_inventory_failed_80070005"] },
            "no_device" => snap with { Endpoints = [] },
            _ => snap with { Processes = [root with { Identity = null }] }
        });
        var a = new WindowsCallSourceAdapter(f, CallSourceKind.Zoom);
        var c = await One(a);
        Assert.Equal(expected, (await a.ValidateAsync(c, default)).Validity);
        await Assert.ThrowsAsync<CallSourceSelectionException>(() => a.ResolveAsync(c, default));
    }

    [Fact]
    public async Task InventoryTimeoutIsNotMisreportedAsAppExit()
    {
        var f = new Fixture(Snapshot([P(10)]));
        var a = new WindowsCallSourceAdapter(f, CallSourceKind.Zoom);
        var c = await One(a);
        f.Current = Snapshot([]) with { ProcessesComplete = false, Diagnostics = ["inventory_timeout"] };
        Assert.Equal(SourceValidity.InventoryUnavailable, (await a.ValidateAsync(c, default)).Validity);
    }

    [Fact]
    public async Task ManualSelectionIncludesSilentAppsAndWarnsForBrowsers()
    {
        var f = new Fixture(Snapshot([P(30, name: "chrome.exe"), P(31, 30, "chrome.exe", 110), P(40, name: "phone.exe")]));
        var a = new WindowsCallSourceAdapter(f, CallSourceKind.Manual);
        var cs = await a.DiscoverAsync(default);
        Assert.Equal(3, cs.Count);
        Assert.Contains("other tabs", cs.Single(c => c.ProcessId == 30).SelectionHint);
        Assert.Equal(new[] { 30, 31 }, cs.Single(c => c.ProcessId == 30).ProcessTree);
        Assert.Equal(40, (await a.ResolveAsync(cs.Single(c => c.ProcessId == 40), default)).Source.ProcessId);
    }

    [Fact]
    public async Task NameSubstringIsNotAppIdentity()
    {
        var f = new Fixture(Snapshot([P(10, name: "fakezoom.exe"), P(11, name: "teams-helper.exe")]));
        Assert.Empty(await new WindowsCallSourceAdapter(f, CallSourceKind.Zoom).DiscoverAsync(default));
        Assert.Empty(await new WindowsCallSourceAdapter(f, CallSourceKind.Teams).DiscoverAsync(default));
    }

    [Fact]
    public async Task MutatedOrForeignCandidatesCannotRedirectSelection()
    {
        var f = new Fixture(Snapshot([P(10), P(20, name: "music.exe")]));
        var a = new WindowsCallSourceAdapter(f, CallSourceKind.Zoom);
        var c = await One(a);
        Assert.Equal("candidate_not_offered", (await a.ValidateAsync(c with { ProcessId = 20 }, default)).DiagnosticCode);
        c.ProcessTree[0] = 20;
        Assert.Equal("candidate_not_offered", (await a.ValidateAsync(c, default)).DiagnosticCode);
        var fresh = await One(a);
        Assert.True((await a.ValidateAsync(fresh, default)).CanSelect);
        Assert.Equal("candidate_not_offered", (await new WindowsCallSourceAdapter(f, CallSourceKind.Zoom).ValidateAsync(fresh, default)).DiagnosticCode);
    }

    [Fact]
    public async Task LeasePinsIdentityAndReportsExitAndDisposal()
    {
        var f = new Fixture(Snapshot([P(10)]));
        var a = new WindowsCallSourceAdapter(f, CallSourceKind.Zoom);
        var c = await One(a);
        var choice = await a.AcquireAsync(c, default);
        choice.EnsureAlive();
        Assert.Equal(1, f.Pins);
        f.Lease.Alive = false;
        Assert.Throws<CallSourceSelectionException>(choice.EnsureAlive);
        choice.Dispose();
        Assert.True(f.Lease.Disposed);
        Assert.False(choice.IsAlive);
    }

    [Fact]
    public async Task PinRaceRejectsChangedIdentity()
    {
        var f = new Fixture(Snapshot([P(10)])) { RejectPin = true };
        var a = new WindowsCallSourceAdapter(f, CallSourceKind.Zoom);
        var c = await One(a);
        await Assert.ThrowsAsync<CallSourceSelectionException>(() => a.AcquireAsync(c, default));
    }

    [Fact]
    public async Task SharedSessionWithCreatorOutsideTreeCannotProveIsolation()
    {
        var root = P(10);
        var unrelatedCreator = P(20, name: "shared.exe");
        var f = new Fixture(Snapshot([root, unrelatedCreator], S(unrelatedCreator, multi: true)));
        var a = new WindowsCallSourceAdapter(f, CallSourceKind.Zoom);
        Assert.Equal(SourceValidity.Ambiguous, (await a.ValidateAsync(await One(a), default)).Validity);
    }

    [Fact]
    public async Task ExitedDuringPinReleasesLease()
    {
        var f = new Fixture(Snapshot([P(10)]));
        f.Lease.Alive = false;
        var a = new WindowsCallSourceAdapter(f, CallSourceKind.Zoom);
        var c = await One(a);
        await Assert.ThrowsAsync<CallSourceSelectionException>(() => a.AcquireAsync(c, default));
        Assert.True(f.Lease.Disposed);
    }

    [Fact]
    public async Task CancellationDoesNotResolveOrPin()
    {
        var f = new Fixture(Snapshot([P(10)]));
        var a = new WindowsCallSourceAdapter(f, CallSourceKind.Zoom);
        var c = await One(a);
        using var ct = new CancellationTokenSource();
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.DiscoverAsync(ct.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.ProbeAsync(c, ct.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.ResolveAsync(c, ct.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.AcquireAsync(c, ct.Token));
        Assert.Equal(0, f.Pins);
    }
}
