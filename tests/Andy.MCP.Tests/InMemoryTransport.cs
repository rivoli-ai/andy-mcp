using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;

namespace Andy.MCP.Tests;

/// <summary>
/// In-memory transport pair for testing. Creates a connected client+server transport
/// where messages sent by one side are received by the other.
/// </summary>
public static class InMemoryTransport
{
    public static (InMemoryClientTransport client, InMemoryServerTransport server) CreatePair()
    {
        var clientToServer = Channel.CreateUnbounded<JsonRpcMessage>();
        var serverToClient = Channel.CreateUnbounded<JsonRpcMessage>();

        var client = new InMemoryClientTransport(clientToServer.Writer, serverToClient.Reader);
        var server = new InMemoryServerTransport(clientToServer.Reader, serverToClient.Writer);

        return (client, server);
    }
}

public sealed class InMemoryClientTransport : IClientTransport
{
    private readonly ChannelWriter<JsonRpcMessage> _outgoing;
    private readonly ChannelReader<JsonRpcMessage> _incoming;
    private volatile bool _connected;

    public bool IsConnected => _connected;
    public event EventHandler<TransportDisconnectedEventArgs>? Disconnected;

    internal InMemoryClientTransport(ChannelWriter<JsonRpcMessage> outgoing, ChannelReader<JsonRpcMessage> incoming)
    {
        _outgoing = outgoing;
        _incoming = incoming;
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _connected = true;
        return Task.CompletedTask;
    }

    public async Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        if (!_connected) throw new InvalidOperationException("Not connected");
        await _outgoing.WriteAsync(message, cancellationToken);
    }

    public IAsyncEnumerable<JsonRpcMessage> Messages => ReadAsync();

    private async IAsyncEnumerable<JsonRpcMessage> ReadAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var msg in _incoming.ReadAllAsync(ct))
            yield return msg;
    }

    public void SimulateDisconnect()
    {
        _connected = false;
        _outgoing.TryComplete();
        Disconnected?.Invoke(this, new TransportDisconnectedEventArgs { Reason = "Simulated disconnect" });
    }

    public ValueTask DisposeAsync()
    {
        _connected = false;
        _outgoing.TryComplete();
        return ValueTask.CompletedTask;
    }
}

public sealed class InMemoryServerTransport : IServerTransport
{
    private readonly ChannelReader<JsonRpcMessage> _incoming;
    private readonly ChannelWriter<JsonRpcMessage> _outgoing;
    private volatile bool _connected;

    public bool IsConnected => _connected;
#pragma warning disable CS0067 // Event is never used
    public event EventHandler<TransportDisconnectedEventArgs>? Disconnected;
#pragma warning restore CS0067

    internal InMemoryServerTransport(ChannelReader<JsonRpcMessage> incoming, ChannelWriter<JsonRpcMessage> outgoing)
    {
        _incoming = incoming;
        _outgoing = outgoing;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _connected = true;
        return Task.CompletedTask;
    }

    public async Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        if (!_connected) throw new InvalidOperationException("Not started");
        await _outgoing.WriteAsync(message, cancellationToken);
    }

    public IAsyncEnumerable<JsonRpcMessage> Messages => ReadAsync();

    private async IAsyncEnumerable<JsonRpcMessage> ReadAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var msg in _incoming.ReadAllAsync(ct))
            yield return msg;
    }

    public ValueTask DisposeAsync()
    {
        _connected = false;
        _outgoing.TryComplete();
        return ValueTask.CompletedTask;
    }
}
