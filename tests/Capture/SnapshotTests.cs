using System.Diagnostics;
using MeetingCompanion.Capture;
using MeetingCompanion.Contracts;

namespace MeetingCompanion.Capture.Tests;

public sealed class SnapshotTests
{
    [Fact]
    public void NegativeMonitorOriginsAndFractionalPhysicalPixels()
    {
        var monitor = new PixelRegion(-1920, -1080, 1920, 1080);
        Assert.Equal(new PixelRegion(119, 179, 82, 102), RegionGeometry.FromScreenPoints(monitor, -1800.2, -900.4, -1719.1, -799.2));
        Assert.Equal(new PixelRegion(119, 179, 82, 102), RegionGeometry.FromScreenPoints(monitor, -1719.1, -799.2, -1800.2, -900.4));
        Assert.Equal(new PixelRegion(0, 0, 1920, 1080), RegionGeometry.FromScreenPoints(monitor, -3000, -2000, 100, 100));
        Assert.Null(RegionGeometry.FromScreenPoints(monitor, 10, 10, 20, 20));
    }

    private sealed class FakeBackend : ISnapshotBackend
    {
        public Func<CancellationToken, Task<CapturedPixels>> Read = _ => Task.FromResult(new CapturedPixels(Enumerable.Repeat((byte)255, 64).ToArray(), 4, 4, Stopwatch.GetTimestamp()));
        public int Calls;
        public Task<CapturedPixels> CaptureAsync(SnapshotTarget target, CancellationToken token) { Calls++; return Read(token); }
    }
    private static SnapshotTarget Target(PixelRegion? crop = null) => new(crop == null ? SnapshotSource.SelectedWindow : SnapshotSource.Region, 1, "Fixture", crop);

    [Fact]
    public async Task EncodesPngWithValidatedFrozenMetadata()
    {
        var result = await new SnapshotCapture(new FakeBackend(), SnapshotCapture.NewClock()).CaptureAsync(Target(new(1, 1, 2, 3)), default);
        result.Metadata.Validate();
        Assert.Equal(2, result.Metadata.Width);
        Assert.Equal(3, result.Metadata.Height);
        Assert.Equal(new PixelRegion(1, 1, 2, 3), result.Metadata.Crop);
        Assert.True(result.Preview.IsFrozen);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, result.Png[..8]);
    }
    [Theory]
    [InlineData(-1, 0, 2, 2)]
    [InlineData(0, -1, 2, 2)]
    [InlineData(3, 3, 2, 2)]
    [InlineData(0, 0, 0, 2)]
    [InlineData(0, 0, 2, 0)]
    [InlineData(int.MaxValue, 0, 2, 2)]
    public async Task RejectsInvalidCrop(int x, int y, int width, int height)
    {
        var capture = new SnapshotCapture(new FakeBackend(), SnapshotCapture.NewClock());
        var error = await Assert.ThrowsAsync<SnapshotFailure>(() => capture.CaptureAsync(Target(new(x, y, width, height)), default));
        Assert.Equal("invalid_region", error.Code);
    }
    [Fact]
    public async Task InvalidSourceNeverCallsBackend()
    {
        var backend = new FakeBackend();
        var capture = new SnapshotCapture(backend, SnapshotCapture.NewClock());
        await Assert.ThrowsAsync<SnapshotFailure>(() => capture.CaptureAsync(Target() with { Handle = 0 }, default));
        await Assert.ThrowsAsync<SnapshotFailure>(() => capture.CaptureAsync(Target() with { Source = SnapshotSource.Region }, default));
        Assert.Equal(0, backend.Calls);
    }
    [Fact]
    public async Task BlankAndMalformedFramesFailThenRecover()
    {
        var backend = new FakeBackend();
        var capture = new SnapshotCapture(backend, SnapshotCapture.NewClock());
        backend.Read = _ => Task.FromResult(new CapturedPixels(new byte[64], 4, 4, Stopwatch.GetTimestamp()));
        Assert.Equal("blank_frame", (await Assert.ThrowsAsync<SnapshotFailure>(() => capture.CaptureAsync(Target(), default))).Code);
        backend.Read = _ => Task.FromResult(new CapturedPixels(new byte[1], 4, 4, Stopwatch.GetTimestamp()));
        Assert.Equal("invalid_frame", (await Assert.ThrowsAsync<SnapshotFailure>(() => capture.CaptureAsync(Target(), default))).Code);
        backend.Read = new FakeBackend().Read;
        await capture.CaptureAsync(Target(), default);
    }
    [Theory]
    [InlineData("timeout")]
    [InlineData("access_denied")]
    [InlineData("closed_window")]
    [InlineData("resized")]
    public async Task FailureDoesNotPoisonNextManualRequest(string code)
    {
        var backend = new FakeBackend { Read = _ => throw new SnapshotFailure(code, "Fixture failure") };
        var capture = new SnapshotCapture(backend, SnapshotCapture.NewClock());
        Assert.Equal(code, (await Assert.ThrowsAsync<SnapshotFailure>(() => capture.CaptureAsync(Target(), default))).Code);
        backend.Read = new FakeBackend().Read;
        await capture.CaptureAsync(Target(), default);
    }
    [Fact]
    public async Task CancellationAndConcurrentRequestAreBounded()
    {
        var entered = new TaskCompletionSource();
        var backend = new FakeBackend { Read = async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); } };
        var capture = new SnapshotCapture(backend, SnapshotCapture.NewClock());
        using var cancellation = new CancellationTokenSource();
        var first = capture.CaptureAsync(Target(), cancellation.Token);
        await entered.Task;
        Assert.Equal("busy", (await Assert.ThrowsAsync<SnapshotFailure>(() => capture.CaptureAsync(Target(), default))).Code);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        backend.Read = new FakeBackend().Read;
        await capture.CaptureAsync(Target(), default);
    }
    [Fact]
    public async Task PrecancelledRequestNeverCaptures()
    {
        var backend = new FakeBackend();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SnapshotCapture(backend, SnapshotCapture.NewClock()).CaptureAsync(Target(), cancelled.Token));
        Assert.Equal(0, backend.Calls);
    }
    private sealed class FakeHotkey : IHotkeyApi
    {
        public bool Available = true;
        public int Unregistered;
        public bool Register(nint window, int id, uint modifiers, uint key) => Available;
        public void Unregister(nint window, int id) => Unregistered++;
    }
    [Fact]
    public void HotkeyCollisionRebindAndDispose()
    {
        var api = new FakeHotkey();
        var hotkey = new SnapshotHotkey(api, 1);
        Assert.True(hotkey.Rebind(6, 'S'));
        Assert.True(hotkey.IsMessage(0x312, 0x4C08));
        Assert.False(hotkey.IsMessage(0x312, 1));
        api.Available = false;
        Assert.False(hotkey.Rebind(6, 'T'));
        Assert.False(hotkey.IsMessage(0x312, 0x4C08));
        api.Available = true;
        Assert.True(hotkey.Rebind(6, 'U'));
        hotkey.Dispose();
        hotkey.Dispose();
        Assert.Equal(2, api.Unregistered);
    }
}
