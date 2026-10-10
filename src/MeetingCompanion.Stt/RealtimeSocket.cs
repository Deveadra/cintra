using System.Net.WebSockets;

namespace MeetingCompanion.Stt;

public readonly record struct SocketRead(int Count, bool EndOfMessage, WebSocketMessageType MessageType);

/// <summary>Small transport seam for deterministic offline provider tests.</summary>
public interface IRealtimeSocket : IAsyncDisposable
{
    Task ConnectAsync(Uri endpoint, string apiKey, CancellationToken cancellationToken);
    Task SendTextAsync(string json, CancellationToken cancellationToken);
    Task<SocketRead> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    Task CloseAsync(CancellationToken cancellationToken);
}

public interface IRealtimeSocketFactory
{
    IRealtimeSocket Create();
}

public sealed class OpenAiRealtimeSocketFactory : IRealtimeSocketFactory
{
    public IRealtimeSocket Create() => new OpenAiRealtimeSocket();
}

internal sealed class OpenAiRealtimeSocket : IRealtimeSocket
{
    private readonly ClientWebSocket socket = new();

    public async Task ConnectAsync(Uri endpoint, string apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("API key is required.", nameof(apiKey));
        socket.Options.SetRequestHeader("Authorization", "Bearer " + apiKey);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
    }

    public Task SendTextAsync(string json, CancellationToken cancellationToken) =>
        socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cancellationToken);

    public async Task<SocketRead> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
        return new SocketRead(result.Count, result.EndOfMessage, result.MessageType);
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "stopped", cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
