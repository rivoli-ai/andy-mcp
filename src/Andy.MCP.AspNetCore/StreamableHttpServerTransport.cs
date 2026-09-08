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
    /// Present origins are denied unless explicitly allowed.
    /// </summary>
    public Func<string?, bool>? ValidateOrigin { get; init; }
    /// <summary>Explicit opt-out for trusted local deployments. Origin checks still apply.</summary>
    public bool AllowAnonymous { get; init; }
    /// <summary>Required policy; ASP.NET Core must validate token signatures/lifetimes first.</summary>
    public McpHttpAuthorizationOptions? Authorization { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Session timeout. Sessions inactive longer than this are cleaned up.
    /// </summary>
    public TimeSpan SessionTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Maximum accepted request body size in bytes. Larger bodies are rejected with 413.
    /// </summary>
    public long MaxRequestBodyBytes { get; init; } = 4 * 1024 * 1024; // 4 MB

    /// <summary>
    /// Maximum number of concurrent sessions. New initialize requests beyond this are rejected
    /// with 503 to bound memory use.
    /// </summary>
    public int MaxSessions { get; init; } = 10_000;
    /// <summary>Close an idle GET stream after this duration so clients can poll/resume.</summary>
    public TimeSpan SsePollTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Reconnection delay sent in the SSE priming event.</summary>
    public int SseRetryMilliseconds { get; init; } = 1000;
    public int MaxConcurrentStreamsPerSession { get; init; } = 16;
}

/// <summary>
/// Handles Streamable HTTP MCP requests. Manages sessions and dispatches to server transports.
/// Designed to be registered as a singleton and mapped to an endpoint.
/// </summary>
public sealed class StreamableHttpHandler : IDisposable
{
    private readonly ConcurrentDictionary<string, StreamableHttpSession> _sessions = new();
    private readonly object _sessionGate = new();
    private readonly ITimer _cleanupTimer;
    private bool _disposed;
    private readonly StreamableHttpServerOptions _options;
    private readonly Func<IServerTransport, Task> _sessionHandler;
    private readonly ILogger<StreamableHttpHandler> _logger;

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
        if (_options.SessionTimeout <= TimeSpan.Zero || _options.MaxSessions <= 0 || _options.MaxRequestBodyBytes <= 0 ||
            _options.SsePollTimeout <= TimeSpan.Zero || _options.SseRetryMilliseconds < 0 || _options.MaxConcurrentStreamsPerSession <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        _options.Authorization?.Validate();
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(_options.SessionTimeout.TotalMilliseconds, 100, 60_000));
        _cleanupTimer = _options.TimeProvider.CreateTimer(_ => RemoveExpiredSessions(), null, interval, interval);
    }

    public async Task HandleAsync(HttpContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var origins = context.Request.Headers.Origin;
        if (origins.Count > 1 || (origins.Count == 1 &&
            (_options.ValidateOrigin is null || !_options.ValidateOrigin(origins[0]))))
        {
            context.Response.StatusCode = 403;
            return;
        }
        if (!_options.AllowAnonymous)
        {
            var auth = _options.Authorization;
            if (auth is null)
            {
                context.Response.StatusCode = 503;
                return;
            }
            var user = context.User;
            if (user.Identity?.IsAuthenticated != true || GetUserKey(context) is null ||
                !user.HasClaim("iss", auth.Issuer) || !user.HasClaim("aud", auth.Resource))
            {
                context.Response.StatusCode = 401;
                context.Response.Headers.WWWAuthenticate = auth.Challenge("invalid_token");
                return;
            }
            var scopes = user.FindAll("scope").Concat(user.FindAll("scp"))
                .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .ToHashSet(StringComparer.Ordinal);
            if (auth.RequiredScopes.Any(scope => !scopes.Contains(scope)))
            {
                context.Response.StatusCode = 403;
                context.Response.Headers.WWWAuthenticate = auth.Challenge("insufficient_scope");
                return;
            }
        }
        RemoveExpiredSessions();

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

        var body = await ReadBodyAsync(context);
        if (body is null)
        {
            context.Response.StatusCode = 413; // Payload Too Large
            return;
        }

        JsonRpcMessage message;
        try
        {
            message = McpJsonDefaults.Deserialize(body);
        }
        catch (Exception ex) when (ex is JsonException or JsonRpcParseException)
        {
            context.Response.StatusCode = 400;
            context.Response.ContentType = "application/json";
            var error = ex is JsonRpcParseException ? JsonRpcError.InvalidRequest() : JsonRpcError.ParseError();
            await context.Response.WriteAsync(
                McpJsonDefaults.Serialize(new JsonRpcUncorrelatedError { Error = error }));
            return;
        }

        // Handle initialize — create new session
        if (message is JsonRpcRequest { Method: McpMethods.Initialize } initRequest)
        {
            StreamableHttpSession session;
            string sessionId;
            lock (_sessionGate)
            {
                if (_disposed || _sessions.Count >= _options.MaxSessions)
                {
                    context.Response.StatusCode = 503;
                    return;
                }
                sessionId = GenerateSessionId();
                session = new StreamableHttpSession(sessionId, GetUserKey(context));
                session.LastActivity = _options.TimeProvider.GetUtcNow();
                session.MaxConcurrentStreams = _options.MaxConcurrentStreamsPerSession;
                _sessions[sessionId] = session;
            }

            context.Response.Headers["Mcp-Session-Id"] = sessionId;

            // Start session handler in background
            _ = Task.Run(async () =>
            {
                try { await _sessionHandler(session); }
                catch (Exception ex) { _logger.LogError(ex, "Session {Id} handler error", sessionId); }
                finally { _sessions.TryRemove(sessionId, out _); session.Close(); }
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

        var lastEventIds = context.Request.Headers["Last-Event-ID"];
        if (lastEventIds.Count > 1) { context.Response.StatusCode = 400; return; }
        var lastEventId = lastEventIds.FirstOrDefault();
        var resume = !string.IsNullOrEmpty(lastEventId);
        string streamId;
        long resumeAfterSeq;
        if (resume)
        {
            if (!StreamableHttpSession.TryParseEventId(lastEventId, out streamId, out resumeAfterSeq))
            { context.Response.StatusCode = 400; return; }
        }
        else { streamId = StreamableHttpSession.NewStreamId(); resumeAfterSeq = 0; }
        if (!session.TryOpenServerStream(streamId, resumeAfterSeq, resume, out var close, out var status))
        { context.Response.StatusCode = status; return; }

        using var poll = new CancellationTokenSource(_options.SsePollTimeout, _options.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, poll.Token, close!.Token);
        var ct = linked.Token;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        var writer = new SseWriter(context.Response.Body);
        try
        {
            // Establish a resumable stream identity even before the first application message.
            await writer.WriteEventAsync(new SseEvent
            {
                Data = "",
                Id = StreamableHttpSession.FormatEventId(streamId, resumeAfterSeq),
                Retry = _options.SseRetryMilliseconds
            }, ct);
            await foreach (var (seq, message) in session.ReadServerEventsCoreAsync(streamId, resumeAfterSeq, ct))
                await writer.WriteEventAsync(new SseEvent
                {
                    Data = McpJsonDefaults.Serialize(message),
                    Id = StreamableHttpSession.FormatEventId(streamId, seq)
                }, ct);
        }
        catch (OperationCanceledException) { }
        finally { session.ReleaseServerStream(streamId, close!); }
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

        session.LastActivity = _options.TimeProvider.GetUtcNow();
        session.MaxConcurrentStreams = _options.MaxConcurrentStreamsPerSession;
        return session;
    }

    private void RemoveExpiredSessions()
    {
        lock (_sessionGate)
        {
            var now = _options.TimeProvider.GetUtcNow();
            foreach (var pair in _sessions)
                if (now - pair.Value.LastActivity >= _options.SessionTimeout &&
                    _sessions.TryRemove(pair.Key, out var expired))
                    expired.Close();
        }
    }

    public void Dispose()
    {
        lock (_sessionGate)
        {
            if (_disposed) return;
            _disposed = true;
            _cleanupTimer.Dispose();
            foreach (var session in _sessions.Values) session.Close();
            _sessions.Clear();
        }
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

        var subject = user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(subject)) return null;
        return JsonSerializer.Serialize(new[] { user.FindFirst("iss")?.Value, subject });
    }

    /// <summary>
    /// Read the request body as UTF-8, enforcing the configured maximum size. Returns null if the
    /// body exceeds the limit (via Content-Length or during streaming).
    /// </summary>
    private async Task<string?> ReadBodyAsync(HttpContext context)
    {
        var max = _options.MaxRequestBodyBytes;
        if (context.Request.ContentLength is { } declared && declared > max)
            return null;

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            if (buffer.Length + read > max)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
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

    // Server-initiated messages destined for a GET SSE stream are held in a bounded replay buffer
    // (rather than a consume-once channel) so a reconnecting client can resume with Last-Event-ID.
    // Each buffered event is claimed by exactly one stream when first delivered, so with multiple
    // concurrent GET streams every message is delivered exactly once. Event ids are
    // "{streamId}.{seq}"; a reconnect reuses the stream id to resume that stream's own events.
    private readonly object _sseSync = new();
    private readonly LinkedList<SseEntry> _replay = new();
    private long _seq;
    private readonly Dictionary<string, CancellationTokenSource> _activeStreams = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownStreams = new(StringComparer.Ordinal);
    internal int MaxConcurrentStreams { get; set; } = 16;
    internal int ActiveStreamCount { get { lock (_sseSync) return _activeStreams.Count; } }
    private const int MaxReplayEvents = 256;
    private TaskCompletionSource _sseSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class SseEntry
    {
        public required long Seq { get; init; }
        public required JsonRpcMessage Message { get; init; }
        public string? ClaimedBy { get; set; }
    }

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

    private long _lastActivityTicks;
    internal DateTimeOffset LastActivity
    {
        get => new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);
        set => Interlocked.Exchange(ref _lastActivityTicks, value.UtcTicks);
    }
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
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Send a message from the server. Responses are routed to the waiting POST handler.
    /// Other messages (requests, notifications) go to the SSE stream.
    /// </summary>
    public Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        if (message is JsonRpcResponse response)
        {
            lock (_sync)
            {
                // Route the response to the waiting POST handler.
                if (_pendingResponses.Remove(response.Id, out var tcs))
                {
                    tcs.TrySetResult(response);
                    return Task.CompletedTask;
                }

                // No waiter is registered yet. A JSON-RPC response is the reply to a
                // client request that arrived on a POST, and MUST be returned on that
                // POST — it must NEVER be leaked onto the GET SSE stream. Because the
                // POST handler registers its waiter before the request becomes visible
                // to the server loop, reaching here is unexpected; buffer the response
                // so a (racing) waiter can still claim it rather than dropping it.
                _bufferedResponses[response.Id] = response;
            }
            return Task.CompletedTask;
        }

        // Server-initiated requests and notifications go to the SSE stream (via the replay buffer).
        AppendServerEvent(message);
        return Task.CompletedTask;
    }

    private void AppendServerEvent(JsonRpcMessage message)
    {
        TaskCompletionSource signal;
        lock (_sseSync)
        {
            _seq++;
            _replay.AddLast(new SseEntry { Seq = _seq, Message = message });
            while (_replay.Count > MaxReplayEvents)
                _replay.RemoveFirst();

            signal = _sseSignal;
            _sseSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        signal.TrySetResult();
    }

    /// <summary>
    /// Messages from the MCP server meant for the SSE stream (server-initiated requests/notifications).
    /// </summary>
    /// <summary>Start a new SSE stream and read its server-initiated messages.</summary>
    public IAsyncEnumerable<JsonRpcMessage> ServerMessages => ReadServerMessagesAsync();

    private async IAsyncEnumerable<JsonRpcMessage> ReadServerMessagesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var (_, message) in ReadServerEventsAsync(NewStreamId(), 0, ct))
            yield return message;
    }

    /// <summary>Generate an identifier for a new SSE stream.</summary>
    public static string NewStreamId() => Guid.NewGuid().ToString("N");

    /// <summary>Format an SSE event id encoding the delivering stream and the event sequence.</summary>
    public static string FormatEventId(string streamId, long seq) => $"{streamId}.{seq}";

    /// <summary>Parse a Last-Event-ID into its stream id and sequence.</summary>
    public static bool TryParseEventId(string? eventId, out string streamId, out long seq)
    {
        streamId = "";
        seq = 0;
        if (eventId is null)
            return false;
        var dot = eventId.LastIndexOf('.');
        if (dot <= 0)
            return false;
        if (!long.TryParse(eventId[(dot + 1)..], out seq) || seq < 0)
            return false;
        streamId = eventId[..dot];
        return true;
    }

    /// <summary>
    /// Reserve one stream identity and enforce session stream limits.
    /// </summary>
    internal bool TryOpenServerStream(string id, long after, bool resume,
        out CancellationTokenSource? close, out int status)
    {
        close = null; status = 200;
        lock (_sseSync)
        {
            if (after < 0 || after > _seq || (resume && !_knownStreams.Contains(id))) { status = 400; return false; }
            if (_activeStreams.ContainsKey(id)) { status = 409; return false; }
            if (_activeStreams.Count >= MaxConcurrentStreams) { status = 429; return false; }
            if (_knownStreams.Count >= Math.Max(MaxReplayEvents, MaxConcurrentStreams) && !_knownStreams.Contains(id))
            {
                var oldest = _knownStreams.FirstOrDefault(s => !_activeStreams.ContainsKey(s));
                if (oldest is not null) _knownStreams.Remove(oldest);
            }
            _knownStreams.Add(id);
            close = new CancellationTokenSource(); _activeStreams.Add(id, close); return true;
        }
    }

    internal void ReleaseServerStream(string id, CancellationTokenSource close)
    {
        lock (_sseSync)
            if (_activeStreams.TryGetValue(id, out var current) && ReferenceEquals(current, close)) _activeStreams.Remove(id);
        close.Dispose();
    }

    /// <summary>Close active GET streams; clients resume using their own last event IDs.</summary>
    public void CloseServerStreams()
    {
        CancellationTokenSource[] streams;
        lock (_sseSync) streams = _activeStreams.Values.ToArray();
        foreach (var stream in streams)
            try { stream.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async IAsyncEnumerable<(long Seq, JsonRpcMessage Message)> ReadServerEventsAsync(
        string streamId, long resumeAfterSeq, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!TryOpenServerStream(streamId, resumeAfterSeq, resumeAfterSeq > 0, out var close, out var status))
            throw new InvalidOperationException($"SSE stream is unavailable ({status}).");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, close!.Token);
        try
        {
            await foreach (var item in ReadServerEventsCoreAsync(streamId, resumeAfterSeq, linked.Token)) yield return item;
        }
        finally { ReleaseServerStream(streamId, close); }
    }

    internal async IAsyncEnumerable<(long Seq, JsonRpcMessage Message)> ReadServerEventsCoreAsync(
        string streamId, long resumeAfterSeq, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Phase 1: resume this stream's own previously-delivered events.
        while (true)
        {
            SseEntry? replayEntry;
            lock (_sseSync)
            {
                replayEntry = _replay.FirstOrDefault(e => e.ClaimedBy == streamId && e.Seq > resumeAfterSeq);
            }
            if (replayEntry is null)
                break;
            resumeAfterSeq = replayEntry.Seq;
            yield return (replayEntry.Seq, replayEntry.Message);
        }

        // Phase 2: claim and deliver unclaimed events (exactly-once across streams).
        while (true)
        {
            SseEntry? claimed;
            Task signal;
            lock (_sseSync)
            {
                claimed = _replay.FirstOrDefault(e => e.ClaimedBy is null);
                if (claimed is not null)
                    claimed.ClaimedBy = streamId;
                signal = _sseSignal.Task;
            }

            if (claimed is not null)
            {
                yield return (claimed.Seq, claimed.Message);
                continue;
            }

            if (!_connected)
                yield break;

            try { await signal.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
        }
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

        // Wake any SSE reader so it observes the closed state and completes.
        TaskCompletionSource signal;
        lock (_sseSync)
        {
            signal = _sseSignal;
        }
        signal.TrySetResult();

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
