using System.Buffers.Binary;

namespace MeetingCompanion.Contracts;

/// <summary>Four-byte little-endian length followed by UTF-8 JSON. Single reader/single writer per stream.</summary>
public static class IpcFraming
{
    public static async ValueTask WriteAsync(Stream stream, IpcEnvelope envelope, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var payload = ContractJson.SerializeIpc(envelope);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        using var deadline = CreateDeadline(timeout, cancellationToken);
        await stream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
        await stream.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
        await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
    }

    /// <summary>Null is a clean EOF between envelopes. Partial envelopes throw; reconnect with a fresh stream.</summary>
    public static async ValueTask<IpcEnvelope?> ReadAsync(Stream stream, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CreateDeadline(timeout, cancellationToken);
        var header = new byte[4];
        if (await stream.ReadAsync(header.AsMemory(0, 1), deadline.Token).ConfigureAwait(false) == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), deadline.Token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        Require.That(length is > 0 and <= ContractVersion.MaxWireBytes, "Invalid IPC frame length.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, deadline.Token).ConfigureAwait(false);
        return ContractJson.DeserializeIpc(payload);
    }

    private static CancellationTokenSource CreateDeadline(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeout.TotalMilliseconds, uint.MaxValue - 1d);
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        return deadline;
    }
}
