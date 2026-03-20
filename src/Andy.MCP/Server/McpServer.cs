using System.Text.Json;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Andy.MCP.Server;

/// <summary>
/// High-level MCP server that handles lifecycle, dispatches requests to registered
/// tools/resources/prompts, and manages capabilities automatically.
/// </summary>
public sealed class McpServer : IAsyncDisposable
{
    private readonly IServerTransport _transport;
    private readonly McpServerOptions _options;
    private readonly ILogger _logger;
    private readonly McpSession _session = new();
    private readonly Dictionary<string, ToolHandler> _tools = new();
    private readonly Dictionary<string, ResourceHandler> _resources = new();
    private readonly Dictionary<string, PromptHandler> _prompts = new();
    private readonly PaginationHelper _pagination;
    private CancellationTokenSource? _cts;
    private Task? _messageLoop;
    private bool _disposed;

    public McpSession Session => _session;

    public McpServer(IServerTransport transport, McpServerOptions? options = null, ILogger? logger = null)
    {
        _transport = transport;
        _options = options ?? new McpServerOptions();
        _logger = logger ?? NullLogger.Instance;
        _pagination = new PaginationHelper(Guid.NewGuid().ToString("N"), _options.PageSize);
    }

    #region Registration

    public McpServer AddTool(string name, string description, JsonElement inputSchema,
        Func<JsonElement?, CancellationToken, Task<CallToolResult>> handler)
    {
        _tools[name] = new ToolHandler
        {
            Tool = new Tool { Name = name, Description = description, InputSchema = inputSchema },
            Handler = handler
        };
        return this;
    }

    public McpServer AddTool(string name, string description,
        Func<JsonElement?, CancellationToken, Task<CallToolResult>> handler)
    {
        var emptySchema = McpJsonDefaults.ToElement(new { type = "object", properties = new { } });
        return AddTool(name, description, emptySchema, handler);
    }

    public McpServer AddResource(string uri, string name,
        Func<string, CancellationToken, Task<ResourceContents>> handler)
    {
        _resources[uri] = new ResourceHandler
        {
            Resource = new Resource { Uri = uri, Name = name },
            Handler = handler
        };
        return this;
    }

    public McpServer AddPrompt(string name, string description,
        Func<string, IDictionary<string, string>?, CancellationToken, Task<GetPromptResult>> handler)
    {
        _prompts[name] = new PromptHandler
        {
            Prompt = new Prompt { Name = name, Description = description },
            Handler = handler
        };
        return this;
    }

    #endregion

    /// <summary>
    /// Start the server and process messages until cancelled.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await _transport.StartAsync(_cts.Token);
        _logger.LogInformation("MCP server started: {Name} v{Version}", _options.ServerInfo.Name, _options.ServerInfo.Version);

        await MessageLoopAsync(_cts.Token);
    }

    private async Task MessageLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _transport.Messages.WithCancellation(ct))
            {
                try
                {
                    await HandleMessageAsync(message, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Error handling message");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandleMessageAsync(JsonRpcMessage message, CancellationToken ct)
    {
        switch (message)
        {
            case JsonRpcRequest request:
                var response = await HandleRequestAsync(request, ct);
                await _transport.SendAsync(response, ct);
                break;

            case JsonRpcNotification notification:
                HandleNotification(notification);
                break;
        }
    }

    private async Task<JsonRpcResponse> HandleRequestAsync(JsonRpcRequest request, CancellationToken ct)
    {
        try
        {
            return request.Method switch
            {
                McpMethods.Initialize => HandleInitialize(request),
                McpMethods.Ping => JsonRpcResponse.Success(request.Id),
                McpMethods.ToolsList => HandleToolsList(request),
                McpMethods.ToolsCall => await HandleToolsCallAsync(request, ct),
                McpMethods.ResourcesList => HandleResourcesList(request),
                McpMethods.ResourcesRead => await HandleResourcesReadAsync(request, ct),
                McpMethods.PromptsList => HandlePromptsList(request),
                McpMethods.PromptsGet => await HandlePromptsGetAsync(request, ct),
                _ => JsonRpcResponse.Failure(request.Id,
                    JsonRpcError.MethodNotFound($"Unknown method: '{request.Method}'"))
            };
        }
        catch (McpPaginationException ex)
        {
            return JsonRpcResponse.Failure(request.Id, JsonRpcError.InvalidParams(ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error handling request {Method}", request.Method);
            return JsonRpcResponse.Failure(request.Id, JsonRpcError.InternalError(ex.Message));
        }
    }

    #region Request Handlers

    private JsonRpcResponse HandleInitialize(JsonRpcRequest request)
    {
        _session.Transition(McpSessionState.Initializing);

        var clientParams = request.GetParams<InitializeParams>()!;
        var agreedVersion = McpSession.NegotiateVersion(clientParams.ProtocolVersion)!;

        var capabilities = BuildCapabilities();

        _session.CompleteInitializationAsServer(clientParams, agreedVersion);

        var result = new InitializeResult
        {
            ProtocolVersion = agreedVersion,
            Capabilities = capabilities,
            ServerInfo = _options.ServerInfo,
            Instructions = _options.Instructions
        };

        return JsonRpcResponse.Success(request.Id, McpJsonDefaults.ToElement(result));
    }

    private JsonRpcResponse HandleToolsList(JsonRpcRequest request)
    {
        var paginatedReq = request.GetParams<PaginatedRequest>() ?? new PaginatedRequest();
        var allTools = _tools.Values.Select(h => h.Tool).ToList();
        var page = _pagination.GetPage(allTools, paginatedReq.Cursor);

        var result = new { tools = page.Items, nextCursor = page.NextCursor };
        return JsonRpcResponse.Success(request.Id, McpJsonDefaults.ToElement(result));
    }

    private async Task<JsonRpcResponse> HandleToolsCallAsync(JsonRpcRequest request, CancellationToken ct)
    {
        var callReq = request.GetParams<CallToolRequest>()!;

        if (!_tools.TryGetValue(callReq.Name, out var handler))
        {
            return JsonRpcResponse.Failure(request.Id,
                JsonRpcError.InvalidParams($"Unknown tool: '{callReq.Name}'"));
        }

        try
        {
            var result = await handler.Handler(callReq.Arguments, ct);
            return JsonRpcResponse.Success(request.Id, McpJsonDefaults.ToElement(result));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var errorResult = CallToolResult.Error(ex.Message);
            return JsonRpcResponse.Success(request.Id, McpJsonDefaults.ToElement(errorResult));
        }
    }

    private JsonRpcResponse HandleResourcesList(JsonRpcRequest request)
    {
        var paginatedReq = request.GetParams<PaginatedRequest>() ?? new PaginatedRequest();
        var allResources = _resources.Values.Select(h => h.Resource).ToList();
        var page = _pagination.GetPage(allResources, paginatedReq.Cursor);

        var result = new { resources = page.Items, nextCursor = page.NextCursor };
        return JsonRpcResponse.Success(request.Id, McpJsonDefaults.ToElement(result));
    }

    private async Task<JsonRpcResponse> HandleResourcesReadAsync(JsonRpcRequest request, CancellationToken ct)
    {
        var uri = request.Params?.GetProperty("uri").GetString();
        if (uri is null)
            return JsonRpcResponse.Failure(request.Id, JsonRpcError.InvalidParams("Missing 'uri' parameter"));

        if (!_resources.TryGetValue(uri, out var handler))
            return JsonRpcResponse.Failure(request.Id, JsonRpcError.ResourceNotFound($"Resource not found: '{uri}'"));

        var contents = await handler.Handler(uri, ct);
        var result = new ReadResourceResult { Contents = [contents] };
        return JsonRpcResponse.Success(request.Id, McpJsonDefaults.ToElement(result));
    }

    private JsonRpcResponse HandlePromptsList(JsonRpcRequest request)
    {
        var paginatedReq = request.GetParams<PaginatedRequest>() ?? new PaginatedRequest();
        var allPrompts = _prompts.Values.Select(h => h.Prompt).ToList();
        var page = _pagination.GetPage(allPrompts, paginatedReq.Cursor);

        var result = new { prompts = page.Items, nextCursor = page.NextCursor };
        return JsonRpcResponse.Success(request.Id, McpJsonDefaults.ToElement(result));
    }

    private async Task<JsonRpcResponse> HandlePromptsGetAsync(JsonRpcRequest request, CancellationToken ct)
    {
        var name = request.Params?.GetProperty("name").GetString();
        if (name is null)
            return JsonRpcResponse.Failure(request.Id, JsonRpcError.InvalidParams("Missing 'name' parameter"));

        if (!_prompts.TryGetValue(name, out var handler))
            return JsonRpcResponse.Failure(request.Id, JsonRpcError.InvalidParams($"Unknown prompt: '{name}'"));

        var arguments = request.Params?.TryGetProperty("arguments", out var args) == true
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(args, McpJsonDefaults.Options)
            : null;

        var result = await handler.Handler(name, arguments, ct);
        return JsonRpcResponse.Success(request.Id, McpJsonDefaults.ToElement(result));
    }

    #endregion

    private void HandleNotification(JsonRpcNotification notification)
    {
        switch (notification.Method)
        {
            case McpMethods.NotificationsInitialized:
                _logger.LogInformation("Client initialized");
                break;
            case McpMethods.NotificationsCancelled:
                _logger.LogDebug("Received cancellation notification");
                break;
            default:
                _logger.LogDebug("Unhandled notification: {Method}", notification.Method);
                break;
        }
    }

    private ServerCapabilities BuildCapabilities() => new()
    {
        Tools = _tools.Count > 0 ? new ListChangedCapability { ListChanged = true } : null,
        Resources = _resources.Count > 0 ? new ResourcesCapability { Subscribe = false, ListChanged = true } : null,
        Prompts = _prompts.Count > 0 ? new ListChangedCapability { ListChanged = true } : null,
    };

    /// <summary>
    /// Send a notification to all connected clients.
    /// </summary>
    public async Task NotifyToolsChangedAsync()
    {
        await _transport.SendAsync(new JsonRpcNotification { Method = McpMethods.NotificationsToolsListChanged });
    }

    public async Task NotifyResourceUpdatedAsync(string uri)
    {
        await _transport.SendAsync(new JsonRpcNotification
        {
            Method = McpMethods.NotificationsResourcesUpdated,
            Params = McpJsonDefaults.ToElement(new { uri })
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        try { if (_messageLoop is not null) await _messageLoop; } catch { }
        await _transport.DisposeAsync();
        _cts?.Dispose();
    }

    #region Internal types

    private sealed class ToolHandler
    {
        public required Tool Tool { get; init; }
        public required Func<JsonElement?, CancellationToken, Task<CallToolResult>> Handler { get; init; }
    }

    private sealed class ResourceHandler
    {
        public required Resource Resource { get; init; }
        public required Func<string, CancellationToken, Task<ResourceContents>> Handler { get; init; }
    }

    private sealed class PromptHandler
    {
        public required Prompt Prompt { get; init; }
        public required Func<string, IDictionary<string, string>?, CancellationToken, Task<GetPromptResult>> Handler { get; init; }
    }

    #endregion
}

/// <summary>
/// Configuration for an MCP server.
/// </summary>
public sealed record McpServerOptions
{
    public Implementation ServerInfo { get; init; } = new("Andy.MCP.Server", "0.1.0");
    public string? Instructions { get; init; }
    public int PageSize { get; init; } = 50;
}
