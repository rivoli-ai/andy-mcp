using System.Text.Json;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Andy.MCP.Server;

/// <summary>
/// High-level MCP server that handles lifecycle, dispatches requests to registered
/// tools/resources/prompts/completions, manages capabilities, subscriptions, and logging.
/// </summary>
public sealed class McpServer : IAsyncDisposable
{
    private readonly IServerTransport _transport;
    private readonly McpServerOptions _options;
    private readonly ILogger _logger;
    private readonly McpSession _session = new();
    private readonly Dictionary<string, ToolHandler> _tools = new();
    private readonly Dictionary<string, ResourceHandler> _resources = new();
    private readonly List<ResourceTemplate> _resourceTemplates = new();
    private readonly Dictionary<string, PromptHandler> _prompts = new();
    private readonly List<CompletionRegistration> _completions = new();
    private readonly ResourceSubscriptionManager _subscriptions = new();
    private readonly PaginationHelper _pagination;
    private McpLogLevel _logLevel = McpLogLevel.Warning;
    private bool _loggingEnabled;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public McpSession Session => _session;
    public ResourceSubscriptionManager Subscriptions => _subscriptions;

    public McpServer(IServerTransport transport, McpServerOptions? options = null, ILogger? logger = null)
    {
        _transport = transport;
        _options = options ?? new McpServerOptions();
        _logger = logger ?? NullLogger.Instance;
        _pagination = new PaginationHelper(Guid.NewGuid().ToString("N"), _options.PageSize);
    }

    #region Registration

    public McpServer AddTool(string name, string description, JsonElement inputSchema,
        Func<JsonElement?, CancellationToken, Task<CallToolResult>> handler,
        ToolAnnotations? annotations = null)
    {
        _tools[name] = new ToolHandler
        {
            Tool = new Tool { Name = name, Description = description, InputSchema = inputSchema, Annotations = annotations },
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
        Func<string, CancellationToken, Task<ResourceContents>> handler,
        string? description = null, string? mimeType = null)
    {
        _resources[uri] = new ResourceHandler
        {
            Resource = new Resource { Uri = uri, Name = name, Description = description, MimeType = mimeType },
            Handler = handler
        };
        return this;
    }

    public McpServer AddResourceTemplate(string uriTemplate, string name, string? description = null, string? mimeType = null)
    {
        _resourceTemplates.Add(new ResourceTemplate
        {
            UriTemplate = uriTemplate,
            Name = name,
            Description = description,
            MimeType = mimeType
        });
        return this;
    }

    public McpServer AddPrompt(string name, string description,
        Func<string, IDictionary<string, string>?, CancellationToken, Task<GetPromptResult>> handler,
        IReadOnlyList<PromptArgument>? arguments = null)
    {
        _prompts[name] = new PromptHandler
        {
            Prompt = new Prompt { Name = name, Description = description, Arguments = arguments },
            Handler = handler
        };
        return this;
    }

    public McpServer AddCompletion(string refType, string refName, string argumentName,
        Func<string, IDictionary<string, string>?, CancellationToken, Task<CompletionValues>> handler)
    {
        _completions.Add(new CompletionRegistration
        {
            RefType = refType,
            RefName = refName,
            ArgumentName = argumentName,
            Handler = handler
        });
        return this;
    }

    public McpServer WithLogging()
    {
        _loggingEnabled = true;
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
        using var activity = McpDiagnostics.StartServerRequest(request.Method, request.Id);

        // Add feature-specific attributes
        if (request.Method == McpMethods.ToolsCall)
        {
            var toolName = request.Params?.TryGetProperty("name", out var n) == true ? n.GetString() : null;
            activity?.SetTag("mcp.tool.name", toolName);
        }
        else if (request.Method == McpMethods.ResourcesRead)
        {
            var uri = request.Params?.TryGetProperty("uri", out var u) == true ? u.GetString() : null;
            activity?.SetTag("mcp.resource.uri", uri);
        }
        else if (request.Method == McpMethods.PromptsGet)
        {
            var name = request.Params?.TryGetProperty("name", out var pn) == true ? pn.GetString() : null;
            activity?.SetTag("mcp.prompt.name", name);
        }

        try
        {
            var response = request.Method switch
            {
                McpMethods.Initialize => HandleInitialize(request),
                McpMethods.Ping => JsonRpcResponse.Success(request.Id),
                McpMethods.ToolsList => HandleToolsList(request),
                McpMethods.ToolsCall => await HandleToolsCallAsync(request, ct),
                McpMethods.ResourcesList => HandleResourcesList(request),
                McpMethods.ResourcesRead => await HandleResourcesReadAsync(request, ct),
                McpMethods.ResourcesTemplatesList => HandleResourcesTemplatesList(request),
                McpMethods.ResourcesSubscribe => HandleResourcesSubscribe(request),
                McpMethods.ResourcesUnsubscribe => HandleResourcesUnsubscribe(request),
                McpMethods.PromptsList => HandlePromptsList(request),
                McpMethods.PromptsGet => await HandlePromptsGetAsync(request, ct),
                McpMethods.CompletionComplete => await HandleCompletionAsync(request, ct),
                McpMethods.LoggingSetLevel => HandleSetLogLevel(request),
                _ => JsonRpcResponse.Failure(request.Id,
                    JsonRpcError.MethodNotFound($"Unknown method: '{request.Method}'"))
            };

            if (response.IsError)
                McpDiagnostics.SetError(activity, errorCode: response.Error?.Code);
            else
                McpDiagnostics.SetSuccess(activity);

            return response;
        }
        catch (McpPaginationException ex)
        {
            McpDiagnostics.SetError(activity, ex);
            return JsonRpcResponse.Failure(request.Id, JsonRpcError.InvalidParams(ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            McpDiagnostics.SetError(activity, ex);
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

        // Validate input against schema
        var validationErrors = JsonSchemaValidator.Validate(callReq.Arguments, handler.Tool.InputSchema);
        if (validationErrors.Count > 0)
        {
            return JsonRpcResponse.Failure(request.Id,
                JsonRpcError.InvalidParams($"Input validation failed: {string.Join("; ", validationErrors)}"));
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

    private JsonRpcResponse HandleResourcesTemplatesList(JsonRpcRequest request)
    {
        var paginatedReq = request.GetParams<PaginatedRequest>() ?? new PaginatedRequest();
        var page = _pagination.GetPage(_resourceTemplates, paginatedReq.Cursor);

        var result = new { resourceTemplates = page.Items, nextCursor = page.NextCursor };
        return JsonRpcResponse.Success(request.Id, McpJsonDefaults.ToElement(result));
    }

    private JsonRpcResponse HandleResourcesSubscribe(JsonRpcRequest request)
    {
        var uri = request.Params?.GetProperty("uri").GetString();
        if (uri is null)
            return JsonRpcResponse.Failure(request.Id, JsonRpcError.InvalidParams("Missing 'uri' parameter"));

        _subscriptions.Subscribe(uri);
        return JsonRpcResponse.Success(request.Id);
    }

    private JsonRpcResponse HandleResourcesUnsubscribe(JsonRpcRequest request)
    {
        var uri = request.Params?.GetProperty("uri").GetString();
        if (uri is null)
            return JsonRpcResponse.Failure(request.Id, JsonRpcError.InvalidParams("Missing 'uri' parameter"));

        _subscriptions.Unsubscribe(uri);
        return JsonRpcResponse.Success(request.Id);
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

    private async Task<JsonRpcResponse> HandleCompletionAsync(JsonRpcRequest request, CancellationToken ct)
    {
        var completionReq = request.GetParams<CompletionRequest>();
        if (completionReq is null)
            return JsonRpcResponse.Failure(request.Id, JsonRpcError.InvalidParams("Missing completion params"));

        var refName = completionReq.Ref.Name ?? completionReq.Ref.Uri;
        var registration = _completions.FirstOrDefault(c =>
            c.RefType == completionReq.Ref.Type &&
            c.RefName == refName &&
            c.ArgumentName == completionReq.Argument.Name);

        if (registration is null)
        {
            var result = new CompletionResult
            {
                Completion = new CompletionData { Values = [], HasMore = false }
            };
            return JsonRpcResponse.Success(request.Id, McpJsonDefaults.ToElement(result));
        }

        var values = await registration.Handler(
            completionReq.Argument.Value,
            completionReq.Context?.Arguments as IDictionary<string, string>,
            ct);

        // Enforce max 100 values
        var truncated = values.Values.Count > 100
            ? values.Values.Take(100).ToList()
            : values.Values;

        var completionResult = new CompletionResult
        {
            Completion = new CompletionData
            {
                Values = truncated,
                Total = values.Total ?? (values.Values.Count > 100 ? values.Values.Count : null),
                HasMore = values.HasMore || values.Values.Count > 100
            }
        };
        return JsonRpcResponse.Success(request.Id, McpJsonDefaults.ToElement(completionResult));
    }

    private JsonRpcResponse HandleSetLogLevel(JsonRpcRequest request)
    {
        var p = request.GetParams<SetLogLevelParams>();
        if (p is null)
            return JsonRpcResponse.Failure(request.Id, JsonRpcError.InvalidParams("Missing 'level' parameter"));

        _logLevel = p.Level;
        _logger.LogDebug("Log level set to {Level}", _logLevel);
        return JsonRpcResponse.Success(request.Id);
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
        Resources = _resources.Count > 0 ? new ResourcesCapability { Subscribe = true, ListChanged = true } : null,
        Prompts = _prompts.Count > 0 ? new ListChangedCapability { ListChanged = true } : null,
        Completions = _completions.Count > 0 ? new EmptyCapability() : null,
        Logging = _loggingEnabled ? new EmptyCapability() : null,
    };

    #region Notifications

    public async Task NotifyToolsChangedAsync()
    {
        await _transport.SendAsync(new JsonRpcNotification { Method = McpMethods.NotificationsToolsListChanged });
    }

    public async Task NotifyResourcesChangedAsync()
    {
        await _transport.SendAsync(new JsonRpcNotification { Method = McpMethods.NotificationsResourcesListChanged });
    }

    public async Task NotifyResourceUpdatedAsync(string uri)
    {
        if (_subscriptions.HasSubscribers(uri))
        {
            await _transport.SendAsync(new JsonRpcNotification
            {
                Method = McpMethods.NotificationsResourcesUpdated,
                Params = McpJsonDefaults.ToElement(new { uri })
            });
        }
    }

    public async Task NotifyPromptsChangedAsync()
    {
        await _transport.SendAsync(new JsonRpcNotification { Method = McpMethods.NotificationsPromptsListChanged });
    }

    /// <summary>
    /// Send a log message to the client. Filtered by the client's set log level.
    /// </summary>
    public async Task LogAsync(McpLogLevel level, JsonElement data, string? loggerName = null)
    {
        if (!_loggingEnabled) return;
        if ((int)level > (int)_logLevel) return; // Filter: only send at or above set level

        await _transport.SendAsync(new JsonRpcNotification
        {
            Method = McpMethods.NotificationsMessage,
            Params = McpJsonDefaults.ToElement(new LogMessageParams
            {
                Level = level,
                Logger = loggerName,
                Data = data
            })
        });
    }

    /// <summary>
    /// Convenience: send a text log message.
    /// </summary>
    public Task LogAsync(McpLogLevel level, string message, string? loggerName = null) =>
        LogAsync(level, McpJsonDefaults.ToElement(message), loggerName);

    #endregion

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
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
