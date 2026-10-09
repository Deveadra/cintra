using System.Buffers.Binary;
using MeetingCompanion.Contracts;

namespace MeetingCompanion.Contract.Tests;

public sealed class IpcFramingTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static IpcEnvelope Message() => new(1, Guid.NewGuid(), Guid.NewGuid(), new CaptureControl(CaptureAction.Stop));

    [Fact]
    public async Task FragmentedBackToBackFramesAndCleanEof()
    {
        var one = Message(); var two = Message();
        using var buffer = new MemoryStream();
        await IpcFraming.WriteAsync(buffer, one, Deadline, default);
        await IpcFraming.WriteAsync(buffer, two, Deadline, default);
        using var fragmented = new FragmentedStream(buffer.ToArray());
        Assert.Equal(one, await IpcFraming.ReadAsync(fragmented, Deadline, default));
        Assert.Equal(two, await IpcFraming.ReadAsync(fragmented, Deadline, default));
        Assert.Null(await IpcFraming.ReadAsync(fragmented, Deadline, default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1048577)]
    public async Task InvalidLengthRejectedBeforePayloadAllocation(int length)
    {
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<ContractException>(async () => await IpcFraming.ReadAsync(stream, Deadline, default));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task TruncatedFramesAreNotCleanEof(int length)
    {
        using var buffer = new MemoryStream();
        await IpcFraming.WriteAsync(buffer, Message(), Deadline, default);
        using var truncated = new MemoryStream(buffer.ToArray()[..length]);
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await IpcFraming.ReadAsync(truncated, Deadline, default));
    }

    [Fact]
    public async Task ReadAndWriteObserveCancellation()
    {
        using var stream = new BlockingStream(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await IpcFraming.ReadAsync(stream, Deadline, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await IpcFraming.WriteAsync(stream, Message(), Deadline, canceled.Token));
    }

    [Fact]
    public async Task StalledPeerHasBoundedReadAndWriteDeadlines()
    {
        using var stream = new BlockingStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await IpcFraming.ReadAsync(stream, TimeSpan.FromMilliseconds(30), default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await IpcFraming.WriteAsync(stream, Message(), TimeSpan.FromMilliseconds(30), default));
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }

    private sealed class BlockingStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
