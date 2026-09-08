using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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
    public int IncomingQueueCapacity { get; init; } = 256;
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
    private readonly Channel<IncomingMessage> _incoming;
    private sealed record IncomingMessage(JsonRpcMessage Message, int Generation);
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private JsonRpcRequest? _initializeRequest;
    private int _generation;
    private CancellationTokenSource _generationCancellation = new();
    internal event Action<InitializeResult>? SessionReinitialized;
    private string? _sessionId;
    private string? _negotiatedVersion;
    private RequestId? _initializeId;
    private string? _lastEventId;
    private readonly TaskCompletionSource _sessionReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TimeSpan? _serverRetry;
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

        _incoming = Channel.CreateBounded<IncomingMessage>(new BoundedChannelOptions(_options.IncomingQueueCapacity)
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
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts!.Token);
        await SendCoreAsync(message, true, lifetime.Token);
    }

    private async Task SendCoreAsync(JsonRpcMessage message, bool recover, CancellationToken cancellationToken)
    {
        await _recoveryGate.WaitAsync(cancellationToken);
        var generation = Volatile.Read(ref _generation);
        var sessionId = _sessionId;
        var originalVersion = _negotiatedVersion;
        _recoveryGate.Release();
        if (message is JsonRpcRequest { Method: "initialize" } initialize)
        {
            if (initialize.Params is { } parameters && parameters.TryGetProperty("protocolVersion", out var version) && version.GetString() == "2024-11-05")
                throw new NotSupportedException("Streamable HTTP does not implement legacy 2024-11-05 HTTP+SSE. Use stdio or a supported Streamable HTTP revision.");
            _initializeId = initialize.Id;
            _initializeRequest = initialize;
        }
        var json = McpJsonDefaults.Serialize(message);
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        ApplyHeaders(request, sessionId, originalVersion);
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
            if (message is JsonRpcRequest { Method: "initialize" } && response.Headers.TryGetValues("Mcp-Session-Id", out var sessionIds))
            {
                _sessionId = sessionIds.FirstOrDefault();
                _logger.LogDebug("Session ID: {SessionId}", _sessionId);
            }

            // Handle 404 (expired session)
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                if (recover && sessionId is not null && message is not JsonRpcRequest { Method: "initialize" })
                {
                    await RecoverSessionAsync(sessionId, cancellationToken);
                    if (originalVersion != _negotiatedVersion)
                        throw new McpSessionExpiredException("The recovered session negotiated a different revision. Issue a new typed request.");
                    if (message is not JsonRpcRequest)
                        throw new McpSessionExpiredException("Session recovered; messages tied to the expired session were not replayed.");
                    await SendCoreAsync(message, false, cancellationToken);
                    return;
                }
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
                CaptureNegotiatedVersion(responseMessage);
                await _incoming.Writer.WriteAsync(new IncomingMessage(responseMessage, generation), cancellationToken);
            }
            else if (contentType == "text/event-stream")
            {
                // SSE stream response — parse events
                var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var state = new SseStreamState();
                var expected = message is JsonRpcRequest posted ? posted.Id : (RequestId?)null;
                var complete = await ProcessSseStreamAsync(stream, cancellationToken, state, expected, generation);
                while (!complete && expected is not null)
                {
                    if (state.Cursor is null)
                        throw new IOException("POST SSE ended without a response or resumable event ID.");
                    await Task.Delay(state.Retry ?? _options.SseReconnectDelay, cancellationToken);
                    using var resume = new HttpRequestMessage(HttpMethod.Get, _options.Endpoint);
                    ApplyHeaders(resume, sessionId, originalVersion);
                    resume.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                    resume.Headers.TryAddWithoutValidation("Last-Event-ID", state.Cursor);
                    using var resumed = await _httpClient.SendAsync(resume, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (resumed.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        if (sessionId is not null) await RecoverSessionAsync(sessionId, cancellationToken);
                        throw new McpSessionExpiredException("The POST stream expired before its result was received. The operation outcome is unknown; it was not replayed.");
                    }
                    resumed.EnsureSuccessStatusCode();
                    if (resumed.Content.Headers.ContentType?.MediaType != "text/event-stream")
                        throw new IOException("POST SSE resumption requires text/event-stream.");
                    await using var resumedStream = await resumed.Content.ReadAsStreamAsync(cancellationToken);
                    complete = await ProcessSseStreamAsync(resumedStream, cancellationToken, state, expected, generation);
                }
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
            if (message.Message is JsonRpcResponse || message.Generation == Volatile.Read(ref _generation))
                yield return message.Message;
        }
    }

    /// <summary>
    /// Background task: opens a GET SSE stream for server-initiated messages.
    /// </summary>
    private async Task SseListenLoopAsync(CancellationToken ct)
    {
        // GET cannot race session creation; initialization releases this signal.
        await _sessionReady.Task.WaitAsync(ct);

        while (!ct.IsCancellationRequested && _connected)
        {
            await _recoveryGate.WaitAsync(ct);
            var sessionId = _sessionId;
            var generation = Volatile.Read(ref _generation);
            var version = _negotiatedVersion;
            var cursor = _lastEventId;
            using var streamLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _generationCancellation.Token);
            _recoveryGate.Release();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _options.Endpoint);
                ApplyHeaders(request, sessionId, version);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

                if (cursor is not null)
                    request.Headers.TryAddWithoutValidation("Last-Event-ID", cursor);

                using var response = await _httpClient.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, streamLifetime.Token);

                if (response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
                {
                    _logger.LogDebug("Server does not support GET SSE stream (405)");
                    return; // Don't retry
                }

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("Session expired on GET SSE stream (404)");
                    if (sessionId is null) return;
                    await RecoverSessionAsync(sessionId, ct);
                    continue;
                }

                response.EnsureSuccessStatusCode();

                var stream = await response.Content.ReadAsStreamAsync(streamLifetime.Token);
                await ProcessSseStreamAsync(stream, streamLifetime.Token, generation: generation);
                await Task.Delay(_serverRetry ?? _options.SseReconnectDelay, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SSE listen stream disconnected, reconnecting in {Delay}",
                    _options.SseReconnectDelay);
                await Task.Delay(_serverRetry ?? _options.SseReconnectDelay, ct);
            }
        }
    }

    private sealed class SseStreamState
    {
        public string? Cursor;
        public TimeSpan? Retry;
    }

    private async Task<bool> ProcessSseStreamAsync(Stream stream, CancellationToken ct,
        SseStreamState? state = null, RequestId? expected = null, int generation = 0)
    {
        await foreach (var evt in SseParser.ParseAsync(stream, ct))
        {
            if (evt.Retry is { } retry && retry >= 0)
            {
                var delay = TimeSpan.FromMilliseconds(Math.Clamp(retry, 10, 60_000));
                if (state is null && generation == Volatile.Read(ref _generation)) _serverRetry = delay; else if (state is not null) state.Retry = delay;
            }
            if (evt.Id is not null)
            {
                var cursor = string.IsNullOrEmpty(evt.Id) ? null : evt.Id;
                if (state is null && generation == Volatile.Read(ref _generation)) _lastEventId = cursor; else if (state is not null) state.Cursor = cursor;
            }

            if (evt.EventType != "message" || string.IsNullOrEmpty(evt.Data))
                continue;

            try
            {
                var message = McpJsonDefaults.Deserialize(evt.Data);
                CaptureNegotiatedVersion(message);
                await _incoming.Writer.WriteAsync(new IncomingMessage(message, generation), ct);
                if (expected is { } id && message is JsonRpcResponse terminal && terminal.Id == id)
                    return true;
            }
            catch (Exception ex) when (ex is JsonRpcParseException or JsonException)
            {
                _logger.LogWarning(ex, "Failed to parse SSE event data as JSON-RPC");
            }
        }
        return false;
    }

    /// <summary>
    /// Once the initialize response is seen, remember the negotiated protocol version so that
    /// subsequent requests carry it rather than our preferred latest version.
    /// </summary>
    private void CaptureNegotiatedVersion(JsonRpcMessage message)
    {
        if (_negotiatedVersion is not null)
            return;

        if (message is JsonRpcResponse { Result: { } result } response && response.Id == _initializeId &&
            result.TryGetProperty("protocolVersion", out var pv) &&
            pv.ValueKind == JsonValueKind.String &&
            pv.GetString() is { } version &&
            McpSession.SupportedProtocolVersions.Contains(version))
        {
            if (!StreamableHttpProtocol.SupportedVersions.Contains(version)) throw new NotSupportedException("The negotiated revision is not supported by Streamable HTTP.");
            _negotiatedVersion = version;
            _sessionReady.TrySetResult();
        }
    }

    private async Task RecoverSessionAsync(string expiredSession, CancellationToken ct)
    {
        using var recoveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts!.Token);
        recoveryTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        ct = recoveryTimeout.Token;
        await _recoveryGate.WaitAsync(ct);
        try
        {
            if (_sessionId != expiredSession) return; // Another caller completed the handshake.
            var original = _initializeRequest ?? throw new McpSessionExpiredException("No initialization request is available for recovery.");
            var initialize = original with { Id = (RequestId)Guid.NewGuid().ToString("N") };
            using var request = RecoveryPost(initialize, null, McpSession.LatestProtocolVersion);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            JsonRpcResponse? initialized = null;
            if (response.Content.Headers.ContentType?.MediaType == "application/json")
                initialized = McpJsonDefaults.Deserialize(await response.Content.ReadAsStringAsync(ct)) as JsonRpcResponse;
            else if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var count = 0;
                await foreach (var evt in SseParser.ParseAsync(stream, ct))
                {
                    if (++count > 256) throw new IOException("Initialization SSE frame limit exceeded.");
                    if (string.IsNullOrEmpty(evt.Data)) continue;
                    if (McpJsonDefaults.Deserialize(evt.Data) is JsonRpcResponse result && result.Id == initialize.Id)
                    { initialized = result; break; }
                }
            }
            if (initialized?.Id != initialize.Id || initialized.Error is not null || initialized.Result is null)
                throw new IOException("Session recovery did not return a successful initialize result.");
            var info = initialized.Result.Value.Deserialize<InitializeResult>(McpJsonDefaults.Options)
                ?? throw new IOException("Missing initialize result.");
            if (!StreamableHttpProtocol.SupportedVersions.Contains(info.ProtocolVersion))
                throw new NotSupportedException("Recovered HTTP session selected an unsupported revision.");
            var newSession = response.Headers.TryGetValues("Mcp-Session-Id", out var ids) ? ids.Single() : null;
            using var ready = RecoveryPost(new JsonRpcNotification { Method = McpMethods.NotificationsInitialized }, newSession, info.ProtocolVersion);
            using var accepted = await _httpClient.SendAsync(ready, HttpCompletionOption.ResponseHeadersRead, ct);
            accepted.EnsureSuccessStatusCode();

            _sessionId = newSession;
            _negotiatedVersion = info.ProtocolVersion;
            Interlocked.Increment(ref _generation);
            _lastEventId = null;
            _serverRetry = null;
            var previous = _generationCancellation;
            _generationCancellation = new CancellationTokenSource();
            previous.Cancel();
            previous.Dispose();
            SessionReinitialized?.Invoke(info);
        }
        finally { _recoveryGate.Release(); }
    }

    private HttpRequestMessage RecoveryPost(JsonRpcMessage message, string? session, string version)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        { Content = new StringContent(McpJsonDefaults.Serialize(message), Encoding.UTF8, "application/json") };
        ApplyHeaders(request);
        request.Headers.Remove("Mcp-Session-Id");
        request.Headers.Remove("MCP-Protocol-Version");
        if (session is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", version);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    private void ApplyHeaders(HttpRequestMessage request) => ApplyHeaders(request, _sessionId, _negotiatedVersion);

    private void ApplyHeaders(HttpRequestMessage request, string? sessionId, string? negotiatedVersion)
    {
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version",
            negotiatedVersion ?? McpSession.LatestProtocolVersion);

        if (sessionId is not null)
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);

        if (_options.AdditionalHeaders is not null)
        {
            foreach (var (key, value) in _options.AdditionalHeaders)
                if (!key.Equals("MCP-Protocol-Version", StringComparison.OrdinalIgnoreCase) &&
                    !key.Equals("Mcp-Session-Id", StringComparison.OrdinalIgnoreCase))
                    request.Headers.TryAddWithoutValidation(key, value);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _connected = false;
        _cts?.Cancel();
        await _recoveryGate.WaitAsync();
        _recoveryGate.Release();

        // Send DELETE to terminate session
        if (_sessionId is not null)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, _options.Endpoint);
                ApplyHeaders(request);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
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
        _generationCancellation.Dispose();
    }
}

/// <summary>
/// Thrown when the server returns 404 indicating an expired session.
/// </summary>
public class McpSessionExpiredException : Exception
{
    public McpSessionExpiredException(string message) : base(message) { }
}
