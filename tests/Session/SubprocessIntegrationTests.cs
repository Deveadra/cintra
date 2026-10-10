using System.Diagnostics;
using System.IO;
using MeetingCompanion.Audio;
using MeetingCompanion.Contracts;
using MeetingCompanion.Platform;
using MeetingCompanion.Session;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace MeetingCompanion.Session.Tests;

/// <summary>Real IPC/helper processes with synthetic audio only. No hardware capture or provider network.</summary>
public sealed class SubprocessIntegrationTests
{
    [Fact]
    public async Task ActualNativeAdapterAndDualTranscriberStopRestartWithoutOrphanFixtureHelpers()
    {
        var fixture = new Fixture();
        var captures = new List<NativeAudioCaptureSession>();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MeetingCompanion.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var helper = Path.Combine(root.FullName, "tests", "SessionFakeHelper", "bin", "Release", "net10.0-windows", "Cintra.Session.FakeHelper.exe");
        Assert.True(File.Exists(helper), "Required synthetic helper executable is missing.");
        var mode = Environment.GetEnvironmentVariable("CINTRA_FAKE_HELPER_MODE");
        Environment.SetEnvironmentVariable("CINTRA_FAKE_HELPER_MODE", null);
        try
        {
            var factory = new MeetingRuntimeFactory(helper, () => false, () => throw new InvalidOperationException(),
                () => { var capture = new NativeAudioCaptureSession(helper); captures.Add(capture); return capture; });
            await using var coordinator = new MeetingCoordinator(fixture.Inventory, factory);
            var choice = await fixture.Choose(coordinator);
            await coordinator.StartAsync(choice, ProviderMode.OfflineTest, default);
            Assert.Equal(SessionState.Listening, coordinator.View.State);
            await coordinator.PauseAsync(default);
            await AssertNoHelpers();
            await coordinator.ResumeAsync(default);
            Assert.Equal(SessionState.Listening, coordinator.View.State);
            await coordinator.StopAsync();
            await AssertNoHelpers();
            Assert.Equal(SessionState.Stopped, coordinator.View.State);
            Assert.Empty(coordinator.View.Transcripts);
            Assert.Equal(1, fixture.Inventory.Leases.Single().Disposals);
        }
        finally { Environment.SetEnvironmentVariable("CINTRA_FAKE_HELPER_MODE", mode); }
    }
    private static async Task AssertNoHelpers()
    {
        // Limit to children of this testhost; other test projects may own helpers concurrently.
        var inventory = await new WindowsCallSourceInventory().ReadAsync(default);
        Assert.True(inventory.ProcessesComplete);
        foreach (var child in inventory.Processes.Where(p => p.ParentProcessId == Environment.ProcessId &&
                     p.ExecutableName.Equals("Cintra.Session.FakeHelper.exe", StringComparison.OrdinalIgnoreCase)))
        {
            try { using var process = Process.GetProcessById(child.ProcessId); Assert.True(process.HasExited, $"Owned synthetic helper still executing: PID {process.Id}."); }
            catch (ArgumentException) { /* Exited after inventory enumeration. */ }
        }
    }
}
