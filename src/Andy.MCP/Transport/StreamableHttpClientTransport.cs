using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Andy.MCP.Protocol;
using Andy.MCP.Transport.Sse;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Andy.MCP.Transport;

/// <summary>
/// Configuration for the Streamable HTTP client transport.
/// </summary>
public sealed record StreamableHttpClientTransportOptions
{
    /// <summary>
    /// The MCP server endpoint URL.
    /// </summary>
    public required Uri Endpoint { get; init; }

    /// <summary>
    /// Optional HttpClient instance (for DI/testing). If null, a new one is created.
    /// </summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>
    /// Additional headers to include on every request.
    /// </summary>
    public IDictionary<string, string>? AdditionalHeaders { get; init; }

    /// <summary>
    /// Whether to open a GET SSE stream for server-initiated messages.
    /// </summary>
    public bool EnableServerSseStream { get; init; } = true;

    /// <summary>
    /// Delay before reconnecting the SSE stream after disconnect.
    /// </summary>
    public TimeSpan SseReconnectDelay { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// MCP client transport using the Streamable HTTP protocol (MCP 2025-11-25).
/// POST for sending messages, GET for server-initiated SSE stream.
/// </summary>
public sealed class StreamableHttpClientTransport : IClientTransport
{
    private readonly StreamableHttpClientTransportOptions _options;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Channel<JsonRpcMessage> _incoming;
    private string? _sessionId;
    private string? _lastEventId;
    private Task? _sseListenTask;
    private CancellationTokenSource? _cts;
    private volatile bool _connected;
    private volatile bool _disposed;

    public bool IsConnected => _connected;
    public event EventHandler<TransportDisconnectedEventArgs>? Disconnected
    {
        add => _disconnected += value;
        remove => _disconnected -= value;
    }
    private EventHandler<TransportDisconnectedEventArgs>? _disconnected;

    public StreamableHttpClientTransport(StreamableHttpClientTransportOptions options, ILogger? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger.Instance;

        if (options.HttpClient is not null)
        {
            _httpClient = options.HttpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient();
            _ownsHttpClient = true;
        }

        _incoming = Channel.CreateUnbounded<JsonRpcMessage>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = false
        });
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connected) throw new InvalidOperationException("Transport is already connected.");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _connected = true;

        // Optionally start GET SSE stream for server-initiated messages
        if (_options.EnableServerSseStream)
        {
            _sseListenTask = Task.Run(() => SseListenLoopAsync(_cts.Token), _cts.Token);
        }

        return Task.CompletedTask;
    }

    public async Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_connected) throw new InvalidOperationException("Transport is not connected.");

        var json = McpJsonDefaults.Serialize(message);
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        ApplyHeaders(request);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "HTTP POST failed");
            throw;
        }

        using (response)
        {
            // Capture session ID from initialize response
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessionIds))
            {
                _sessionId = sessionIds.FirstOrDefault();
                _logger.LogDebug("Session ID: {SessionId}", _sessionId);
            }

            // Handle 404 (expired session)
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _sessionId = null;
                throw new McpSessionExpiredException("Session expired. Server returned 404.");
            }

            response.EnsureSuccessStatusCode();

            // 202 Accepted — no body (notification/response was accepted)
            if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
                return;

            var contentType = response.Content.Headers.ContentType?.MediaType;

            if (contentType == "application/json")
            {
                // Single JSON response
                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                var responseMessage = McpJsonDefaults.Deserialize(responseJson);
                await _incoming.Writer.WriteAsync(responseMessage, cancellationToken);
            }
            else if (contentType == "text/event-stream")
            {
                // SSE stream response — parse events
                var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await ProcessSseStreamAsync(stream, cancellationToken);
            }
            else
            {
                _logger.LogWarning("Unexpected Content-Type: {ContentType}", contentType);
            }
        }
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

    /// <summary>
    /// Background task: opens a GET SSE stream for server-initiated messages.
    /// </summary>
    private async Task SseListenLoopAsync(CancellationToken ct)
    {
        // Wait briefly for initialization to complete before opening GET stream
        await Task.Delay(500, ct);

        while (!ct.IsCancellationRequested && _connected)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _options.Endpoint);
                ApplyHeaders(request);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

                if (_lastEventId is not null)
                    request.Headers.TryAddWithoutValidation("Last-Event-ID", _lastEventId);

                using var response = await _httpClient.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, ct);

                if (response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
                {
                    _logger.LogDebug("Server does not support GET SSE stream (405)");
                    return; // Don't retry
                }

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("Session expired on GET SSE stream (404)");
                    _sessionId = null;
                    return;
                }

                response.EnsureSuccessStatusCode();

                var stream = await response.Content.ReadAsStreamAsync(ct);
                await ProcessSseStreamAsync(stream, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SSE listen stream disconnected, reconnecting in {Delay}",
                    _options.SseReconnectDelay);
                await Task.Delay(_options.SseReconnectDelay, ct);
            }
        }
    }

    private async Task ProcessSseStreamAsync(Stream stream, CancellationToken ct)
    {
        await foreach (var evt in SseParser.ParseAsync(stream, ct))
        {
            if (evt.Id is not null)
                _lastEventId = evt.Id;

            if (evt.EventType != "message" || string.IsNullOrEmpty(evt.Data))
                continue;

            try
            {
                var message = McpJsonDefaults.Deserialize(evt.Data);
                await _incoming.Writer.WriteAsync(message, ct);
            }
            catch (JsonRpcParseException ex)
            {
                _logger.LogWarning(ex, "Failed to parse SSE event data as JSON-RPC");
            }
        }
    }

    private void ApplyHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", McpSession.LatestProtocolVersion);

        if (_sessionId is not null)
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);

        if (_options.AdditionalHeaders is not null)
        {
            foreach (var (key, value) in _options.AdditionalHeaders)
                request.Headers.TryAddWithoutValidation(key, value);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _connected = false;

        // Send DELETE to terminate session
        if (_sessionId is not null)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, _options.Endpoint);
                ApplyHeaders(request);
                await _httpClient.SendAsync(request, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send session DELETE");
            }
        }

        _incoming.Writer.TryComplete();
        _cts?.Cancel();

        try { if (_sseListenTask is not null) await _sseListenTask; } catch { }

        if (_ownsHttpClient) _httpClient.Dispose();
        _cts?.Dispose();
    }
}

/// <summary>
/// Thrown when the server returns 404 indicating an expired session.
/// </summary>
public class McpSessionExpiredException : Exception
{
    public McpSessionExpiredException(string message) : base(message) { }
}
