using System.Diagnostics;
using MeetingCompanion.Audio;
using MeetingCompanion.Contracts;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace MeetingCompanion.Audio.Tests;

public sealed class AudioTests
{
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MeetingCompanion.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? throw new IOException("Repository root missing.");
        }
    }
    private static string Fake => Path.Combine(Root, "tests", "AudioFakeHelper", "bin", "Release", "net10.0-windows", "Cintra.Audio.FakeHelper.exe");
    private static CaptureSelection Selection => new(new(AudioStreamId.LocalMic, "Fake mic", "fake-device", null, false, false), new(AudioStreamId.RemoteApp, "Fake incoming", null, Environment.ProcessId, true, false));
    private static ClockMapping Clock => new(Stopwatch.GetTimestamp(), Stopwatch.Frequency, DateTimeOffset.UtcNow);

    [Fact]
    public async Task QueuePreservesIdentityAndReportsEachOverflow()
    {
        var inbox = new CaptureInbox();
        for (int i = 0; i < 101; ++i)
            foreach (var stream in new[] { AudioStreamId.LocalMic, AudioStreamId.RemoteApp })
                inbox.Add(new AudioFrame(stream, i, i * 20, i * 20 + 20, AudioFormat.Normalized, 48000, new byte[960]));
        inbox.Complete();
        var messages = new List<CaptureMessage>();
        await foreach (var message in inbox.ReadAllAsync(CancellationToken.None)) messages.Add(message);
        foreach (var stream in new[] { AudioStreamId.LocalMic, AudioStreamId.RemoteApp })
        {
            Assert.Equal(100, messages.OfType<AudioFrame>().Count(x => x.StreamId == stream));
            Assert.Contains(messages.OfType<CaptureGap>(), x => x.StreamId == stream && x.Reason == GapReason.QueueOverflow && x.DroppedFrames == 480);
        }
    }

    [Fact]
    public async Task QueueCancellationAndClearReleaseAudio()
    {
        var inbox = new CaptureInbox();
        inbox.Add(new AudioFrame(AudioStreamId.LocalMic, 0, 0, 20, AudioFormat.Normalized, 48000, new byte[960]));
        inbox.ClearAudio();
        using var cancel = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var ignored in inbox.ReadAllAsync(cancel.Token)) Assert.Fail("Cleared audio escaped.");
        });
        inbox.Complete(true);
    }

    [Fact]
    public async Task AuthenticatedHelperStartPauseResumeStop()
    {
        Environment.SetEnvironmentVariable("CINTRA_FAKE_HELPER_MODE", null);
        await using var session = new NativeAudioCaptureSession(Fake);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await session.StartAsync(Selection, Clock, deadline.Token);
        await using var messages = session.ReadAllAsync(deadline.Token).GetAsyncEnumerator();
        var frames = new List<AudioFrame>();
        while (frames.Count < 2) { Assert.True(await messages.MoveNextAsync()); if (messages.Current is AudioFrame frame) frames.Add(frame); }
        Assert.Equal(2, frames.Select(x => x.StreamId).Distinct().Count());
        await session.PauseAsync(deadline.Token);
        await session.ResumeAsync(deadline.Token);
        frames.Clear();
        while (frames.Count < 2) { Assert.True(await messages.MoveNextAsync()); if (messages.Current is AudioFrame frame) frames.Add(frame); }
        Assert.All(frames, x => Assert.Equal(1, x.Sequence));
        await session.StopAsync(deadline.Token);
        Assert.False(await messages.MoveNextAsync());
        await session.StopAsync(deadline.Token);
    }

    [Theory]
    [InlineData("crash")]
    [InlineData("stall")]
    [InlineData("wrong-session")]
    [InlineData("remote-stall")]
    public async Task HelperFaultProducesBothStreamGaps(string mode)
    {
        Environment.SetEnvironmentVariable("CINTRA_FAKE_HELPER_MODE", mode);
        try
        {
            await using var session = new NativeAudioCaptureSession(Fake);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await session.StartAsync(Selection, Clock, deadline.Token);
            var gaps = new List<CaptureGap>();
            await foreach (var message in session.ReadAllAsync(deadline.Token)) if (message is CaptureGap gap) gaps.Add(gap);
            Assert.Equal(2, gaps.Count(x => x.Reason == GapReason.HelperExited));
        }
        finally { Environment.SetEnvironmentVariable("CINTRA_FAKE_HELPER_MODE", null); }
    }

    [Fact]
    public async Task MissingHelperAndCancelledStartFailWithoutResourceRetention()
    {
        await using var session = new NativeAudioCaptureSession(Path.Combine(Root, "artifacts", "missing-helper.exe"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => session.StartAsync(Selection, Clock, CancellationToken.None));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await using var cancelled = new NativeAudioCaptureSession(Fake);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.StartAsync(Selection, Clock, cancel.Token));
    }

    [Fact]
    public async Task StopTimeoutKillsOnlyOwnedHelper()
    {
        Environment.SetEnvironmentVariable("CINTRA_FAKE_HELPER_MODE", "ignore-stop");
        try
        {
            await using var session = new NativeAudioCaptureSession(Fake);
            await session.StartAsync(Selection, Clock, CancellationToken.None);
            await Assert.ThrowsAsync<IOException>(() => session.StopAsync(CancellationToken.None));
            Assert.Empty(Process.GetProcessesByName("Cintra.Audio.FakeHelper"));
        }
        finally { Environment.SetEnvironmentVariable("CINTRA_FAKE_HELPER_MODE", null); }
    }

    [Fact]
    public async Task RepeatedHelperSessionsReleasePipesAndProcesses()
    {
        for (int i = 0; i < 10; ++i)
        {
            await using var session = new NativeAudioCaptureSession(Fake);
            await session.StartAsync(Selection, Clock, CancellationToken.None);
            await session.PauseAsync(CancellationToken.None);
            await session.ResumeAsync(CancellationToken.None);
            await session.StopAsync(CancellationToken.None);
        }
        Assert.Empty(Process.GetProcessesByName("Cintra.Audio.FakeHelper"));
    }

    [Fact]
    public async Task CancelledStopStillReleasesOwnedHelper()
    {
        await using var session = new NativeAudioCaptureSession(Fake);
        await session.StartAsync(Selection, Clock, CancellationToken.None);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAsync<IOException>(() => session.StopAsync(cancellation.Token));
        Assert.Empty(Process.GetProcessesByName("Cintra.Audio.FakeHelper"));
    }
}
