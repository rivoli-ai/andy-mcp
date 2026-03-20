using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Andy.MCP.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Andy.MCP.Transport;

/// <summary>
/// Server transport that communicates via stdin/stdout.
/// The server reads JSON-RPC messages from stdin and writes responses to stdout.
/// Stderr is available for logging (not used by the transport itself).
/// </summary>
public sealed class StdioServerTransport : IServerTransport
{
    private readonly ILogger _logger;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly Channel<JsonRpcMessage> _outgoing;
    private readonly Channel<JsonRpcMessage> _incoming;
    private Task? _readLoop;
    private Task? _writeLoop;
    private CancellationTokenSource? _cts;
    private volatile bool _connected;
    private volatile bool _disposed;

    public bool IsConnected => _connected;
    public event EventHandler<TransportDisconnectedEventArgs>? Disconnected;

    /// <summary>
    /// Create a stdio server transport using the real Console stdin/stdout.
    /// </summary>
    public StdioServerTransport(ILogger? logger = null)
        : this(Console.In, Console.Out, logger) { }

    /// <summary>
    /// Create a stdio server transport with custom input/output streams (useful for testing).
    /// </summary>
    public StdioServerTransport(TextReader input, TextWriter output, ILogger? logger = null)
    {
        _input = input;
        _output = output;
        _logger = logger ?? NullLogger.Instance;
        _outgoing = Channel.CreateUnbounded<JsonRpcMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _incoming = Channel.CreateUnbounded<JsonRpcMessage>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = false
        });
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connected) throw new InvalidOperationException("Transport is already started.");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _connected = true;

        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token), _cts.Token);
        _writeLoop = Task.Run(() => WriteLoopAsync(_cts.Token), _cts.Token);

        _logger.LogInformation("stdio server transport started");
        return Task.CompletedTask;
    }

    public async Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_connected) throw new InvalidOperationException("Transport is not started.");

        await _outgoing.Writer.WriteAsync(message, cancellationToken);
    }

    public IAsyncEnumerable<JsonRpcMessage> Messages => ReadMessagesAsync();

    private async IAsyncEnumerable<JsonRpcMessage> ReadMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in _incoming.Reader.ReadAllAsync(cancellationToken))
        {
            yield return message;
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _input.ReadLineAsync(ct);
                if (line is null) break; // EOF — client closed stdin

                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var message = McpJsonDefaults.Deserialize(line);
                    await _incoming.Writer.WriteAsync(message, ct);
                }
                catch (JsonRpcParseException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse JSON-RPC message from stdin");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Error deserializing message from stdin");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "stdin read loop error");
        }
        finally
        {
            _connected = false;
            _incoming.Writer.TryComplete();
            Disconnected?.Invoke(this, new TransportDisconnectedEventArgs { Reason = "stdin closed" });
        }
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _outgoing.Reader.ReadAllAsync(ct))
            {
                var json = McpJsonDefaults.Serialize(message);
                await _output.WriteLineAsync(json.AsMemory(), ct);
                await _output.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "stdout write loop error");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _connected = false;
        _outgoing.Writer.TryComplete();

        _cts?.Cancel();

        try { if (_readLoop is not null) await _readLoop.ConfigureAwait(false); } catch { }
        try { if (_writeLoop is not null) await _writeLoop.ConfigureAwait(false); } catch { }

        _cts?.Dispose();
    }
}
