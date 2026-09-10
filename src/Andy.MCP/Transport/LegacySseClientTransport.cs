using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using Andy.MCP.Protocol;
using Andy.MCP.Transport.Sse;

namespace Andy.MCP.Transport;

/// <summary>Compatibility transport for the two-endpoint HTTP+SSE protocol from MCP 2024-11-05.</summary>
public sealed class LegacySseClientTransport : IClientTransport
{
    private readonly Uri _endpoint;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeSpan _timeout;
    private readonly Channel<JsonRpcMessage> _messages = Channel.CreateBounded<JsonRpcMessage>(256);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<Uri> _messageEndpoint = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _reader;
    private int _started, _disposed;
    private volatile bool _connected;
    public bool IsConnected => _connected;
    public event EventHandler<TransportDisconnectedEventArgs>? Disconnected;
    public IAsyncEnumerable<JsonRpcMessage> Messages => _messages.Reader.ReadAllAsync();

    public LegacySseClientTransport(Uri endpoint, HttpClient? httpClient = null, TimeSpan? requestTimeout = null)
    {
        Gateway.McpGatewayClient.ValidateEndpoint(endpoint);
        _endpoint = endpoint;
        _timeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttp = httpClient is null;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Transport has already been started.");
        _reader = ReadAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        timeout.CancelAfter(_timeout);
        try { await _messageEndpoint.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch { await DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task ReadAsync()
    {
        Exception? failure = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _stop.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Legacy MCP endpoint did not return SSE.");
            await using var stream = await response.Content.ReadAsStreamAsync(_stop.Token).ConfigureAwait(false);
            await foreach (var item in SseParser.ParseAsync(stream, _stop.Token).ConfigureAwait(false))
            {
                if (item.EventType == "endpoint")
                {
                    var target = new Uri(_endpoint, item.Data);
                    if (target.Scheme != _endpoint.Scheme || target.Host != _endpoint.Host || target.Port != _endpoint.Port
                        || !string.IsNullOrEmpty(target.UserInfo) || !string.IsNullOrEmpty(target.Fragment))
                        throw new InvalidOperationException("Legacy MCP message endpoint must use the SSE endpoint's origin.");
                    if (_messageEndpoint.Task.IsCompleted) throw new InvalidOperationException("Legacy MCP endpoint changed within a session.");
                    _connected = true;
                    _messageEndpoint.TrySetResult(target);
                }
                else if (item.EventType == "message")
                {
                    if (!_connected) throw new InvalidOperationException("Legacy MCP message arrived before its endpoint.");
                    var message = McpJsonDefaults.Deserialize(item.Data) ?? throw new InvalidOperationException("Empty MCP message.");
                    await _messages.Writer.WriteAsync(message, _stop.Token).ConfigureAwait(false);
                }
            }
            if (!_stop.IsCancellationRequested) throw new IOException("Legacy MCP SSE stream ended.");
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        finally
        {
            _connected = false;
            _messageEndpoint.TrySetException(failure ?? new IOException("Legacy MCP connection closed."));
            _messages.Writer.TryComplete(failure);
            if (failure is not null)
                foreach (EventHandler<TransportDisconnectedEventArgs> handler in Disconnected?.GetInvocationList() ?? [])
                    try { handler(this, new() { Reason = "Legacy MCP connection closed.", Exception = failure }); } catch { }
        }
    }

    public async Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_connected) throw new InvalidOperationException("Legacy MCP transport is not connected.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        timeout.CancelAfter(_timeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, await _messageEndpoint.Task.ConfigureAwait(false))
        {
            Content = new StringContent(McpJsonDefaults.Serialize(message), Encoding.UTF8, "application/json")
        };
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _connected = false;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_reader is not null) await _reader.ConfigureAwait(false);
        _messages.Writer.TryComplete();
        if (_ownsHttp) _http.Dispose();
        _stop.Dispose();
    }
}
