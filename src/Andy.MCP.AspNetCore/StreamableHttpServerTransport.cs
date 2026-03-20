using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;
using Andy.MCP.Transport.Sse;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Andy.MCP.AspNetCore;

/// <summary>
/// Options for the Streamable HTTP server transport.
/// </summary>
public sealed record StreamableHttpServerOptions
{
    /// <summary>
    /// Validate the Origin header. Return true to allow, false to reject.
    /// If null, all origins are allowed.
    /// </summary>
    public Func<string?, bool>? ValidateOrigin { get; init; }

    /// <summary>
    /// Session timeout. Sessions inactive longer than this are cleaned up.
    /// </summary>
    public TimeSpan SessionTimeout { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// Handles Streamable HTTP MCP requests. Manages sessions and dispatches to server transports.
/// Designed to be registered as a singleton and mapped to an endpoint.
/// </summary>
public sealed class StreamableHttpHandler
{
    private readonly ConcurrentDictionary<string, StreamableHttpSession> _sessions = new();
    private readonly StreamableHttpServerOptions _options;
    private readonly Func<IServerTransport, Task> _sessionHandler;
    private readonly ILogger<StreamableHttpHandler> _logger;
    private long _eventCounter;

    /// <param name="sessionHandler">Called when a new session is created. Receives the session's transport
    /// and should run the MCP server logic (e.g., McpServer.RunAsync).</param>
    /// <param name="options">Optional configuration for the Streamable HTTP transport.</param>
    /// <param name="logger">Optional logger instance.</param>
    public StreamableHttpHandler(
        Func<IServerTransport, Task> sessionHandler,
        StreamableHttpServerOptions? options = null,
        ILogger<StreamableHttpHandler>? logger = null)
    {
        _sessionHandler = sessionHandler;
        _options = options ?? new StreamableHttpServerOptions();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<StreamableHttpHandler>.Instance;
    }

    public async Task HandleAsync(HttpContext context)
    {
        // Validate Origin
        if (_options.ValidateOrigin is not null)
        {
            var origin = context.Request.Headers.Origin.FirstOrDefault();
            if (!_options.ValidateOrigin(origin))
            {
                context.Response.StatusCode = 403;
                return;
            }
        }

        // Validate protocol version
        var version = context.Request.Headers["MCP-Protocol-Version"].FirstOrDefault();
        if (version is not null && !McpSession.SupportedProtocolVersions.Contains(version))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Unsupported protocol version");
            return;
        }

        switch (context.Request.Method)
        {
            case "POST":
                await HandlePostAsync(context);
                break;
            case "GET":
                await HandleGetAsync(context);
                break;
            case "DELETE":
                HandleDelete(context);
                break;
            default:
                context.Response.StatusCode = 405;
                break;
        }
    }

    private async Task HandlePostAsync(HttpContext context)
    {
        var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
        JsonRpcMessage message;
        try
        {
            message = McpJsonDefaults.Deserialize(body);
        }
        catch (Exception)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Invalid JSON-RPC message");
            return;
        }

        // Handle initialize — create new session
        if (message is JsonRpcRequest { Method: McpMethods.Initialize } initRequest)
        {
            var sessionId = GenerateSessionId();
            var session = new StreamableHttpSession(sessionId);
            _sessions[sessionId] = session;

            context.Response.Headers["Mcp-Session-Id"] = sessionId;

            // Start session handler in background
            _ = Task.Run(async () =>
            {
                try { await _sessionHandler(session); }
                catch (Exception ex) { _logger.LogError(ex, "Session {Id} handler error", sessionId); }
            });

            // Route message to session and get response
            await session.ReceiveMessageAsync(message);
            var response = await session.WaitForResponseAsync(initRequest.Id, context.RequestAborted);

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(McpJsonDefaults.Serialize(response));
            return;
        }

        // All other requests require a session
        var sid = context.Request.Headers["Mcp-Session-Id"].FirstOrDefault();
        if (sid is null || !_sessions.TryGetValue(sid, out var existingSession))
        {
            context.Response.StatusCode = sid is null ? 400 : 404;
            return;
        }

        if (message is JsonRpcNotification)
        {
            // Notifications: accept and don't respond
            await existingSession.ReceiveMessageAsync(message);
            context.Response.StatusCode = 202;
            return;
        }

        if (message is JsonRpcRequest request)
        {
            await existingSession.ReceiveMessageAsync(message);
            var response = await existingSession.WaitForResponseAsync(request.Id, context.RequestAborted);

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(McpJsonDefaults.Serialize(response));
            return;
        }

        // Response from client (to server-initiated request)
        if (message is JsonRpcResponse clientResponse)
        {
            await existingSession.ReceiveMessageAsync(message);
            context.Response.StatusCode = 202;
        }
    }

    private async Task HandleGetAsync(HttpContext context)
    {
        var sid = context.Request.Headers["Mcp-Session-Id"].FirstOrDefault();
        if (sid is null || !_sessions.TryGetValue(sid, out var session))
        {
            context.Response.StatusCode = sid is null ? 400 : 404;
            return;
        }

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        var writer = new SseWriter(context.Response.Body);
        var ct = context.RequestAborted;

        try
        {
            await foreach (var message in session.ServerMessages.WithCancellation(ct))
            {
                var eventId = Interlocked.Increment(ref _eventCounter).ToString();
                await writer.WriteEventAsync(new SseEvent
                {
                    Data = McpJsonDefaults.Serialize(message),
                    Id = eventId
                }, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void HandleDelete(HttpContext context)
    {
        var sid = context.Request.Headers["Mcp-Session-Id"].FirstOrDefault();
        if (sid is not null && _sessions.TryRemove(sid, out var session))
        {
            session.Close();
            context.Response.StatusCode = 200;
        }
        else
        {
            context.Response.StatusCode = 404;
        }
    }

    private static string GenerateSessionId()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }
}

/// <summary>
/// A per-session transport for the Streamable HTTP server.
/// Receives messages from POST requests and sends responses/notifications.
/// </summary>
public sealed class StreamableHttpSession : IServerTransport
{
    private readonly Channel<JsonRpcMessage> _incoming;
    private readonly Channel<JsonRpcMessage> _outgoing;
    private readonly ConcurrentDictionary<RequestId, TaskCompletionSource<JsonRpcResponse>> _pendingResponses = new();
    private volatile bool _connected = true;

    public string SessionId { get; }
    public bool IsConnected => _connected;
    public event EventHandler<TransportDisconnectedEventArgs>? Disconnected;

    public StreamableHttpSession(string sessionId)
    {
        SessionId = sessionId;
        _incoming = Channel.CreateUnbounded<JsonRpcMessage>();
        _outgoing = Channel.CreateUnbounded<JsonRpcMessage>();
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Send a message from the server. Responses are routed to the waiting POST handler.
    /// Other messages (requests, notifications) go to the SSE stream.
    /// </summary>
    public async Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        if (message is JsonRpcResponse response)
        {
            // Route response to the waiting POST handler
            if (_pendingResponses.TryRemove(response.Id, out var tcs))
            {
                tcs.TrySetResult(response);
                return;
            }
        }

        // Server-initiated requests and notifications go to SSE stream
        await _outgoing.Writer.WriteAsync(message, cancellationToken);
    }

    /// <summary>
    /// Messages from the MCP server meant for the SSE stream (server-initiated requests/notifications).
    /// </summary>
    public IAsyncEnumerable<JsonRpcMessage> ServerMessages => ReadServerMessages();

    private async IAsyncEnumerable<JsonRpcMessage> ReadServerMessages(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var msg in _outgoing.Reader.ReadAllAsync(ct))
            yield return msg;
    }

    /// <summary>
    /// Incoming messages from the client (forwarded by POST handler).
    /// </summary>
    public IAsyncEnumerable<JsonRpcMessage> Messages => ReadMessages();

    private async IAsyncEnumerable<JsonRpcMessage> ReadMessages(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var msg in _incoming.Reader.ReadAllAsync(ct))
            yield return msg;
    }

    /// <summary>
    /// Called by the HTTP handler to deliver a message from a POST request.
    /// </summary>
    internal async Task ReceiveMessageAsync(JsonRpcMessage message)
    {
        await _incoming.Writer.WriteAsync(message);
    }

    /// <summary>
    /// Called by the HTTP handler to wait for the server's response to a specific request.
    /// </summary>
    internal async Task<JsonRpcResponse> WaitForResponseAsync(RequestId requestId, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<JsonRpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingResponses[requestId] = tcs;
        ct.Register(() => tcs.TrySetCanceled());
        return await tcs.Task;
    }

    internal void Close()
    {
        _connected = false;
        _incoming.Writer.TryComplete();
        _outgoing.Writer.TryComplete();
        foreach (var tcs in _pendingResponses.Values)
            tcs.TrySetCanceled();
        _pendingResponses.Clear();
        Disconnected?.Invoke(this, new TransportDisconnectedEventArgs { Reason = "Session closed" });
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }
}
