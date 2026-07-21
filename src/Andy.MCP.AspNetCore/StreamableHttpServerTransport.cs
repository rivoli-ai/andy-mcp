using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Claims;
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
        // The client MUST send application/json and accept both application/json and
        // text/event-stream so the server may reply with either a JSON body or an SSE stream.
        if (!IsJsonContentType(context))
        {
            context.Response.StatusCode = 415; // Unsupported Media Type
            return;
        }

        if (!AcceptsAll(context, "application/json", "text/event-stream"))
        {
            context.Response.StatusCode = 406; // Not Acceptable
            return;
        }

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
            var session = new StreamableHttpSession(sessionId, GetUserKey(context));
            _sessions[sessionId] = session;

            context.Response.Headers["Mcp-Session-Id"] = sessionId;

            // Start session handler in background
            _ = Task.Run(async () =>
            {
                try { await _sessionHandler(session); }
                catch (Exception ex) { _logger.LogError(ex, "Session {Id} handler error", sessionId); }
            });

            // Register the response waiter BEFORE the request becomes visible to the
            // server loop, then deliver the request. This ordering closes the race where a
            // fast server response could miss the waiter and leak onto the SSE stream.
            var responseTask = session.RegisterResponseWaiter(initRequest.Id, context.RequestAborted);
            await session.ReceiveMessageAsync(message);

            JsonRpcResponse response;
            try
            {
                response = await responseTask;
            }
            catch (OperationCanceledException)
            {
                // Client aborted before the response arrived; the waiter is already removed.
                return;
            }

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(McpJsonDefaults.Serialize(response));
            return;
        }

        // All other requests require a session bound to the same authenticated principal.
        var existingSession = ResolveAuthorizedSession(context);
        if (existingSession is null)
            return; // status already set (400/404/403)

        if (message is JsonRpcNotification)
        {
            // Notifications: accept and don't respond
            await existingSession.ReceiveMessageAsync(message);
            context.Response.StatusCode = 202;
            return;
        }

        if (message is JsonRpcRequest request)
        {
            // Register the waiter before delivering the request (see initialize path).
            var responseTask = existingSession.RegisterResponseWaiter(request.Id, context.RequestAborted);
            await existingSession.ReceiveMessageAsync(message);

            JsonRpcResponse response;
            try
            {
                response = await responseTask;
            }
            catch (OperationCanceledException)
            {
                return;
            }

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
        // A GET opens an SSE stream, so the client MUST accept text/event-stream.
        if (!AcceptsAll(context, "text/event-stream"))
        {
            context.Response.StatusCode = 406; // Not Acceptable
            return;
        }

        var session = ResolveAuthorizedSession(context);
        if (session is null)
            return; // status already set (400/404/403)

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
        // Only the owning principal may delete a session.
        var session = ResolveAuthorizedSession(context);
        if (session is null)
            return; // status already set (400/404/403)

        _sessions.TryRemove(session.SessionId, out _);
        session.Close();
        context.Response.StatusCode = 200;
    }

    /// <summary>
    /// Resolve the session named by the Mcp-Session-Id header, enforcing that the current request's
    /// authenticated principal matches the one that created it. Sets the response status and returns
    /// null on any failure: 400 (no session header), 404 (unknown session), 403 (cross-user reuse).
    /// </summary>
    private StreamableHttpSession? ResolveAuthorizedSession(HttpContext context)
    {
        var sid = context.Request.Headers["Mcp-Session-Id"].FirstOrDefault();
        if (sid is null)
        {
            context.Response.StatusCode = 400;
            return null;
        }

        if (!_sessions.TryGetValue(sid, out var session))
        {
            context.Response.StatusCode = 404;
            return null;
        }

        if (!string.Equals(session.UserKey, GetUserKey(context), StringComparison.Ordinal))
        {
            context.Response.StatusCode = 403;
            return null;
        }

        return session;
    }

    /// <summary>
    /// A stable key identifying the request's authenticated principal, or null if unauthenticated.
    /// Used only internally to bind sessions; never surfaced to clients.
    /// </summary>
    private static string? GetUserKey(HttpContext context)
    {
        var user = context.User;
        if (user?.Identity?.IsAuthenticated != true)
            return null;

        return user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("sub")?.Value
            ?? user.Identity.Name;
    }

    private static bool IsJsonContentType(HttpContext context)
    {
        var contentType = context.Request.ContentType;
        return contentType is not null &&
               contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True if the request's Accept header allows every one of <paramref name="mediaTypes"/>
    /// (a <c>*/*</c> wildcard satisfies all). An absent Accept header is treated as not acceptable.
    /// </summary>
    private static bool AcceptsAll(HttpContext context, params string[] mediaTypes)
    {
        var accept = context.Request.Headers.Accept;
        if (accept.Count == 0)
            return false;

        var value = string.Join(",", accept.ToArray());
        if (value.Contains("*/*", StringComparison.Ordinal))
            return true;

        return mediaTypes.All(mt => value.Contains(mt, StringComparison.OrdinalIgnoreCase));
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

    // All response-correlation state is guarded by _sync. _pendingResponses holds waiters
    // registered by POST handlers; _bufferedResponses holds responses that arrived before
    // their waiter was registered (defensive — see RegisterResponseWaiter/SendAsync).
    private readonly object _sync = new();
    private readonly Dictionary<RequestId, TaskCompletionSource<JsonRpcResponse>> _pendingResponses = new();
    private readonly Dictionary<RequestId, JsonRpcResponse> _bufferedResponses = new();
    private volatile bool _connected = true;

    // Test-only visibility into correlation state to assert no waiters/buffers are leaked.
    internal int PendingResponseCount { get { lock (_sync) { return _pendingResponses.Count; } } }
    internal int BufferedResponseCount { get { lock (_sync) { return _bufferedResponses.Count; } } }

    public string SessionId { get; }

    /// <summary>
    /// A stable key for the principal that created this session, or null if it was created
    /// anonymously. Requests bearing a different principal are rejected. Never sent to clients.
    /// </summary>
    public string? UserKey { get; }

    public bool IsConnected => _connected;
    public event EventHandler<TransportDisconnectedEventArgs>? Disconnected;

    public StreamableHttpSession(string sessionId, string? userKey = null)
    {
        SessionId = sessionId;
        UserKey = userKey;
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
            lock (_sync)
            {
                // Route the response to the waiting POST handler.
                if (_pendingResponses.Remove(response.Id, out var tcs))
                {
                    tcs.TrySetResult(response);
                    return;
                }

                // No waiter is registered yet. A JSON-RPC response is the reply to a
                // client request that arrived on a POST, and MUST be returned on that
                // POST — it must NEVER be leaked onto the GET SSE stream. Because the
                // POST handler registers its waiter before the request becomes visible
                // to the server loop, reaching here is unexpected; buffer the response
                // so a (racing) waiter can still claim it rather than dropping it.
                _bufferedResponses[response.Id] = response;
            }
            return;
        }

        // Server-initiated requests and notifications go to the SSE stream.
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
    /// The caller must register a response waiter (for requests) via
    /// <see cref="RegisterResponseWaiter"/> before calling this, so the response cannot
    /// be produced before a waiter exists.
    /// </summary>
    internal async Task ReceiveMessageAsync(JsonRpcMessage message)
    {
        await _incoming.Writer.WriteAsync(message);
    }

    /// <summary>
    /// Registers a waiter for the server's response to a specific request and returns the
    /// task that completes when the response arrives. This MUST be called before the request
    /// is handed to the server loop via <see cref="ReceiveMessageAsync"/> so a fast server
    /// response can never miss the waiter and be routed onto the SSE stream.
    /// </summary>
    internal Task<JsonRpcResponse> RegisterResponseWaiter(RequestId requestId, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<JsonRpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_sync)
        {
            // A response may already be buffered if it raced ahead of registration.
            if (_bufferedResponses.Remove(requestId, out var buffered))
            {
                tcs.SetResult(buffered);
                return tcs.Task;
            }

            if (!_connected)
            {
                tcs.SetCanceled();
                return tcs.Task;
            }

            if (_pendingResponses.ContainsKey(requestId))
                throw new InvalidOperationException(
                    $"A request with id '{requestId}' is already awaiting a response on this session.");

            _pendingResponses[requestId] = tcs;
        }

        if (ct.CanBeCanceled)
        {
            var registration = ct.Register(static state =>
            {
                var (session, id) = ((StreamableHttpSession session, RequestId id))state!;
                session.CancelWaiter(id);
            }, (this, requestId));

            // Dispose the CT registration once the wait completes to avoid accumulating
            // registrations on a long-lived cancellation token.
            tcs.Task.ContinueWith(
                static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
                registration,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return tcs.Task;
    }

    private void CancelWaiter(RequestId requestId)
    {
        TaskCompletionSource<JsonRpcResponse>? tcs;
        lock (_sync)
        {
            _pendingResponses.Remove(requestId, out tcs);
        }
        tcs?.TrySetCanceled();
    }

    internal void Close()
    {
        List<TaskCompletionSource<JsonRpcResponse>> toCancel;
        lock (_sync)
        {
            _connected = false;
            toCancel = _pendingResponses.Values.ToList();
            _pendingResponses.Clear();
            _bufferedResponses.Clear();
        }

        _incoming.Writer.TryComplete();
        _outgoing.Writer.TryComplete();

        foreach (var tcs in toCancel)
            tcs.TrySetCanceled();

        Disconnected?.Invoke(this, new TransportDisconnectedEventArgs { Reason = "Session closed" });
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }
}
