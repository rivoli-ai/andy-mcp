using System.Runtime.CompilerServices;
using System.Text.Json;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Andy.MCP.Client;

/// <summary>
/// Configuration for an MCP client connection.
/// </summary>
public sealed record McpClientOptions
{
    public Implementation ClientInfo { get; init; } = new("Andy.MCP", "0.1.0");
    public ClientCapabilities Capabilities { get; init; } = new();
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Root provider for the roots capability. If set, roots capability is declared.
    /// </summary>
    public IRootProvider? RootProvider { get; init; }

    /// <summary>
    /// Sampling handler for the sampling capability. If set, sampling capability is declared.
    /// </summary>
    public ISamplingHandler? SamplingHandler { get; init; }

    /// <summary>
    /// Elicitation handler for the elicitation capability. If set, elicitation capability is declared.
    /// </summary>
    public IElicitationHandler? ElicitationHandler { get; init; }

    /// <summary>
    /// Build capabilities based on configured handlers.
    /// </summary>
    internal ClientCapabilities BuildCapabilities()
    {
        return new ClientCapabilities
        {
            Roots = RootProvider is not null
                ? new RootsCapability { ListChanged = true }
                : Capabilities.Roots,
            Sampling = SamplingHandler is not null
                ? Capabilities.Sampling ?? new SamplingCapability()
                : Capabilities.Sampling,
            Elicitation = ElicitationHandler is not null
                ? new EmptyCapability()
                : Capabilities.Elicitation,
            Experimental = Capabilities.Experimental
        };
    }
}

/// <summary>
/// High-level MCP client that orchestrates transport, lifecycle, and protocol features.
/// Thread-safe for concurrent operations.
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    private readonly IClientTransport _transport;
    private readonly McpClientOptions _options;
    private readonly ILogger _logger;
    private readonly McpSession _session = new();
    private readonly PendingRequestTracker _tracker = new();
    private long _nextId;
    private Task? _messageLoop;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>
    /// The negotiated session state and capabilities.
    /// </summary>
    public McpSession Session => _session;

    // Events for server notifications
    public event EventHandler? ToolsChanged;
    public event EventHandler? ResourcesChanged;
    public event EventHandler<string>? ResourceUpdated;
    public event EventHandler? PromptsChanged;
    public event EventHandler<LogMessageEventArgs>? LogMessage;
    public event EventHandler<TransportDisconnectedEventArgs>? Disconnected;

    private McpClient(IClientTransport transport, McpClientOptions options, ILogger? logger)
    {
        _transport = transport;
        _options = options;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Connect to an MCP server, perform initialization handshake, and return a ready client.
    /// </summary>
    public static async Task<McpClient> ConnectAsync(
        IClientTransport transport,
        McpClientOptions? options = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new McpClientOptions();
        var client = new McpClient(transport, options, logger);

        try
        {
            await client.InitializeAsync(cancellationToken);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await _transport.ConnectAsync(_cts.Token);
        _transport.Disconnected += OnTransportDisconnected;

        _session.Transition(McpSessionState.Initializing);

        // Start message processing loop
        _messageLoop = Task.Run(() => MessageLoopAsync(_cts.Token), _cts.Token);

        // Wire up root provider change notifications
        if (_options.RootProvider is not null)
        {
            _options.RootProvider.RootsChanged += async (_, _) =>
            {
                try { await SendNotificationAsync(McpMethods.NotificationsRootsListChanged); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to send roots/list_changed"); }
            };
        }

        // Send initialize request
        var initResult = await SendRequestAsync<InitializeResult>(
            McpMethods.Initialize,
            new InitializeParams
            {
                ProtocolVersion = McpSession.LatestProtocolVersion,
                Capabilities = _options.BuildCapabilities(),
                ClientInfo = _options.ClientInfo
            },
            cancellationToken);

        // Validate version
        if (!McpSession.IsVersionAcceptable(initResult.ProtocolVersion))
        {
            throw new McpSessionException(
                $"Server offered protocol version '{initResult.ProtocolVersion}' which is not supported. " +
                $"Supported versions: {string.Join(", ", McpSession.SupportedProtocolVersions)}");
        }

        _session.CompleteInitializationAsClient(initResult);

        // Send initialized notification
        await SendNotificationAsync(McpMethods.NotificationsInitialized);

        _logger.LogInformation(
            "MCP client connected to {ServerName} v{Version}, protocol {Protocol}",
            initResult.ServerInfo.Name, initResult.ServerInfo.Version, initResult.ProtocolVersion);
    }

    #region Public API

    public Task PingAsync(CancellationToken ct = default) =>
        SendRequestAsync<JsonElement>(McpMethods.Ping, null, ct);

    public async Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken ct = default)
    {
        _session.RequireServerCapability("tools");
        var result = await SendRequestAsync<ToolsListResult>(McpMethods.ToolsList, new PaginatedRequest(), ct);
        var all = new List<Tool>(result.Tools);

        var cursor = result.NextCursor;
        while (cursor is not null)
        {
            result = await SendRequestAsync<ToolsListResult>(McpMethods.ToolsList, new PaginatedRequest { Cursor = cursor }, ct);
            all.AddRange(result.Tools);
            cursor = result.NextCursor;
        }

        return all;
    }

    public async Task<CallToolResult> CallToolAsync(string name, object? arguments = null, CancellationToken ct = default)
    {
        _session.RequireServerCapability("tools");
        var args = arguments is JsonElement je
            ? je
            : arguments is not null
                ? McpJsonDefaults.ToElement(arguments)
                : (JsonElement?)null;

        return await SendRequestAsync<CallToolResult>(McpMethods.ToolsCall,
            new CallToolRequest { Name = name, Arguments = args }, ct);
    }

    public async Task<IReadOnlyList<Resource>> ListResourcesAsync(CancellationToken ct = default)
    {
        _session.RequireServerCapability("resources");
        var result = await SendRequestAsync<ResourcesListResult>(McpMethods.ResourcesList, new PaginatedRequest(), ct);
        var all = new List<Resource>(result.Resources);

        var cursor = result.NextCursor;
        while (cursor is not null)
        {
            result = await SendRequestAsync<ResourcesListResult>(McpMethods.ResourcesList, new PaginatedRequest { Cursor = cursor }, ct);
            all.AddRange(result.Resources);
            cursor = result.NextCursor;
        }

        return all;
    }

    public async Task<IReadOnlyList<ResourceTemplate>> ListResourceTemplatesAsync(CancellationToken ct = default)
    {
        _session.RequireServerCapability("resources");
        var result = await SendRequestAsync<ResourceTemplatesListResult>(McpMethods.ResourcesTemplatesList, new PaginatedRequest(), ct);
        var all = new List<ResourceTemplate>(result.ResourceTemplates);

        var cursor = result.NextCursor;
        while (cursor is not null)
        {
            result = await SendRequestAsync<ResourceTemplatesListResult>(McpMethods.ResourcesTemplatesList, new PaginatedRequest { Cursor = cursor }, ct);
            all.AddRange(result.ResourceTemplates);
            cursor = result.NextCursor;
        }

        return all;
    }

    public async Task<ReadResourceResult> ReadResourceAsync(string uri, CancellationToken ct = default)
    {
        _session.RequireServerCapability("resources");
        return await SendRequestAsync<ReadResourceResult>(McpMethods.ResourcesRead,
            new { uri }, ct);
    }

    public async Task<IReadOnlyList<Prompt>> ListPromptsAsync(CancellationToken ct = default)
    {
        _session.RequireServerCapability("prompts");
        var result = await SendRequestAsync<PromptsListResult>(McpMethods.PromptsList, new PaginatedRequest(), ct);
        var all = new List<Prompt>(result.Prompts);

        var cursor = result.NextCursor;
        while (cursor is not null)
        {
            result = await SendRequestAsync<PromptsListResult>(McpMethods.PromptsList, new PaginatedRequest { Cursor = cursor }, ct);
            all.AddRange(result.Prompts);
            cursor = result.NextCursor;
        }

        return all;
    }

    public async Task<GetPromptResult> GetPromptAsync(string name, IDictionary<string, string>? arguments = null, CancellationToken ct = default)
    {
        _session.RequireServerCapability("prompts");
        return await SendRequestAsync<GetPromptResult>(McpMethods.PromptsGet,
            new { name, arguments }, ct);
    }

    public async Task SetLogLevelAsync(string level, CancellationToken ct = default)
    {
        _session.RequireServerCapability("logging");
        await SendRequestAsync<JsonElement>(McpMethods.LoggingSetLevel, new { level }, ct);
    }

    #endregion

    #region Message Sending

    private RequestId NextId() => (RequestId)Interlocked.Increment(ref _nextId);

    private async Task<T> SendRequestAsync<T>(string method, object? @params, CancellationToken ct)
    {
        var id = NextId();
        using var activity = McpDiagnostics.StartClientRequest(
            method, id, _session.RemoteInfo?.Name, _session.ProtocolVersion);

        var request = new JsonRpcRequest
        {
            Id = id,
            Method = method,
            Params = @params is not null ? McpJsonDefaults.ToElement(@params) : null
        };

        var pending = _tracker.Track(id, _options.RequestTimeout);
        try
        {
            await _transport.SendAsync(request, ct);
            var response = await pending.Task.WaitAsync(ct);

            if (response.IsError)
            {
                var ex = new McpException(response.Error!.Code, response.Error.Message, response.Error.Data);
                McpDiagnostics.SetError(activity, ex, response.Error.Code);
                throw ex;
            }

            McpDiagnostics.SetSuccess(activity);

            if (response.Result is null)
                return default!;

            return JsonSerializer.Deserialize<T>(response.Result.Value, McpJsonDefaults.Options)!;
        }
        catch (Exception ex) when (activity is not null && ex is not McpException)
        {
            McpDiagnostics.SetError(activity, ex);
            throw;
        }
        finally
        {
            pending.Dispose();
        }
    }

    private async Task SendNotificationAsync(string method, object? @params = null)
    {
        var notification = new JsonRpcNotification
        {
            Method = method,
            Params = @params is not null ? McpJsonDefaults.ToElement(@params) : null
        };
        await _transport.SendAsync(notification);
    }

    #endregion

    #region Message Loop

    private async Task MessageLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _transport.Messages.WithCancellation(ct))
            {
                try
                {
                    HandleIncomingMessage(message);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error handling incoming message");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Message loop error");
        }
    }

    private void HandleIncomingMessage(JsonRpcMessage message)
    {
        switch (message)
        {
            case JsonRpcResponse response:
                // Correlate with pending request
                if (!_tracker.TryComplete(response.Id, response))
                {
                    _logger.LogDebug("Received response for unknown/completed request {Id}", response.Id);
                }
                break;

            case JsonRpcNotification notification:
                HandleNotification(notification);
                break;

            case JsonRpcRequest serverRequest:
                HandleServerRequest(serverRequest);
                break;
        }
    }

    private void HandleNotification(JsonRpcNotification notification)
    {
        switch (notification.Method)
        {
            case McpMethods.NotificationsToolsListChanged:
                ToolsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case McpMethods.NotificationsResourcesListChanged:
                ResourcesChanged?.Invoke(this, EventArgs.Empty);
                break;
            case McpMethods.NotificationsResourcesUpdated:
                var uri = notification.Params?.GetProperty("uri").GetString();
                if (uri is not null) ResourceUpdated?.Invoke(this, uri);
                break;
            case McpMethods.NotificationsPromptsListChanged:
                PromptsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case McpMethods.NotificationsMessage:
                HandleLogMessage(notification);
                break;
            case McpMethods.NotificationsProgress:
                HandleProgress(notification);
                break;
            case McpMethods.NotificationsCancelled:
                HandleCancellation(notification);
                break;
        }
    }

    private void HandleLogMessage(JsonRpcNotification notification)
    {
        if (notification.Params is null) return;
        var p = notification.Params.Value;
        LogMessage?.Invoke(this, new LogMessageEventArgs
        {
            Level = p.GetProperty("level").GetString()!,
            Logger = p.TryGetProperty("logger", out var l) ? l.GetString() : null,
            Data = p.TryGetProperty("data", out var d) ? d : null
        });
    }

    private void HandleProgress(JsonRpcNotification notification)
    {
        var p = notification.GetParams<ProgressParams>();
        if (p is null) return;
        _tracker.TryReportProgress(p.ProgressToken, p.Progress, p.Total, p.Message);
    }

    private void HandleCancellation(JsonRpcNotification notification)
    {
        var p = notification.GetParams<CancelledParams>();
        if (p is null) return;
        _tracker.TryCancel(p.RequestId, p.Reason);
    }

    private void HandleServerRequest(JsonRpcRequest request)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var response = await DispatchServerRequestAsync(request);
                await _transport.SendAsync(response);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error handling server request '{Method}'", request.Method);
                await _transport.SendAsync(JsonRpcResponse.Failure(request.Id,
                    JsonRpcError.InternalError(ex.Message)));
            }
        });
    }

    private async Task<JsonRpcResponse> DispatchServerRequestAsync(JsonRpcRequest request)
    {
        switch (request.Method)
        {
            case McpMethods.Ping:
                return JsonRpcResponse.Success(request.Id);

            case McpMethods.RootsList:
                if (_options.RootProvider is null)
                    return JsonRpcResponse.Failure(request.Id, JsonRpcError.MethodNotFound("Roots not supported"));

                var roots = _options.RootProvider.GetRoots();
                return JsonRpcResponse.Success(request.Id,
                    McpJsonDefaults.ToElement(new ListRootsResult { Roots = roots }));

            case McpMethods.SamplingCreateMessage:
                if (_options.SamplingHandler is null)
                    return JsonRpcResponse.Failure(request.Id, JsonRpcError.MethodNotFound("Sampling not supported"));

                var samplingReq = request.GetParams<CreateMessageRequest>()!;
                var samplingResult = await _options.SamplingHandler.HandleAsync(samplingReq, _cts?.Token ?? CancellationToken.None);
                return JsonRpcResponse.Success(request.Id, McpJsonDefaults.ToElement(samplingResult));

            case McpMethods.ElicitationCreate:
                if (_options.ElicitationHandler is null)
                    return JsonRpcResponse.Failure(request.Id, JsonRpcError.MethodNotFound("Elicitation not supported"));

                var elicitReq = request.GetParams<ElicitRequest>()!;
                var elicitResult = await _options.ElicitationHandler.HandleAsync(elicitReq, _cts?.Token ?? CancellationToken.None);
                return JsonRpcResponse.Success(request.Id, McpJsonDefaults.ToElement(elicitResult));

            default:
                return JsonRpcResponse.Failure(request.Id,
                    JsonRpcError.MethodNotFound($"Client does not handle '{request.Method}'"));
        }
    }

    #endregion

    private void OnTransportDisconnected(object? sender, TransportDisconnectedEventArgs e)
    {
        _tracker.CancelAll("Transport disconnected");
        Disconnected?.Invoke(this, e);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _tracker.CancelAll("Client disposing");
        _cts?.Cancel();

        try { if (_messageLoop is not null) await _messageLoop; } catch { }

        _tracker.Dispose();
        await _transport.DisposeAsync();
        _cts?.Dispose();
    }
}

/// <summary>
/// Thrown when the server returns a JSON-RPC error response.
/// </summary>
public class McpException : Exception
{
    public int ErrorCode { get; }
    public JsonElement? ErrorData { get; }

    public McpException(int code, string message, JsonElement? data = null)
        : base(message)
    {
        ErrorCode = code;
        ErrorData = data;
    }
}

/// <summary>
/// Log message event data from the server.
/// </summary>
public sealed class LogMessageEventArgs : EventArgs
{
    public required string Level { get; init; }
    public string? Logger { get; init; }
    public JsonElement? Data { get; init; }
}

#region Response DTOs for list operations

public sealed record ToolsListResult : PaginatedResult
{
    [System.Text.Json.Serialization.JsonPropertyName("tools")]
    public IReadOnlyList<Tool> Tools { get; init; } = [];
}

public sealed record ResourcesListResult : PaginatedResult
{
    [System.Text.Json.Serialization.JsonPropertyName("resources")]
    public IReadOnlyList<Resource> Resources { get; init; } = [];
}

public sealed record ResourceTemplatesListResult : PaginatedResult
{
    [System.Text.Json.Serialization.JsonPropertyName("resourceTemplates")]
    public IReadOnlyList<ResourceTemplate> ResourceTemplates { get; init; } = [];
}

public sealed record PromptsListResult : PaginatedResult
{
    [System.Text.Json.Serialization.JsonPropertyName("prompts")]
    public IReadOnlyList<Prompt> Prompts { get; init; } = [];
}

#endregion
