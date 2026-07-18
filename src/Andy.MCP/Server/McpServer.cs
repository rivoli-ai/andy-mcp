using System.Text.Json;
using Andy.MCP.Client;
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
    private readonly PendingRequestTracker _tracker = new();
    private long _nextId;
    private bool _initialized;
    private McpLogLevel _logLevel = McpLogLevel.Warning;
    private bool _loggingEnabled;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public McpSession Session => _session;
    public ResourceSubscriptionManager Subscriptions => _subscriptions;

    /// <summary>
    /// Serialize an outbound result/params payload for the session's negotiated revision, omitting
    /// any fields newer than that revision. Falls back to the latest revision before negotiation.
    /// </summary>
    private JsonElement ToWire<T>(T value) =>
        RevisionAwareJson.ToElementForRevision(value, _session.Revision ?? ProtocolRevision.Latest);

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

            case JsonRpcResponse clientResponse:
                // Correlate a client response to a server-initiated request.
                if (!_tracker.TryComplete(clientResponse.Id, clientResponse))
                    _logger.LogDebug("Received response with no matching pending request: {Id}", clientResponse.Id);
                break;

            case JsonRpcNotification notification:
                HandleNotification(notification);
                break;
        }
    }

    private async Task<JsonRpcResponse> HandleRequestAsync(JsonRpcRequest request, CancellationToken ct)
    {
        using var activity = McpDiagnostics.StartServerRequest(request.Method, request.Id);

        // Add feature-specific attributes. Extraction must be type-safe: a caller may send a
        // wrongly-typed field, and this runs before the guarded handler body.
        if (request.Method == McpMethods.ToolsCall)
            activity?.SetTag("mcp.tool.name", TryGetStringProperty(request.Params, "name"));
        else if (request.Method == McpMethods.ResourcesRead)
            activity?.SetTag("mcp.resource.uri", TryGetStringProperty(request.Params, "uri"));
        else if (request.Method == McpMethods.PromptsGet)
            activity?.SetTag("mcp.prompt.name", TryGetStringProperty(request.Params, "name"));

        try
        {
            // Lifecycle enforcement: initialize is valid only once, and no operation other than
            // ping is accepted until the client's notifications/initialized has been received.
            if (request.Method == McpMethods.Initialize)
            {
                if (_session.State != McpSessionState.Uninitialized)
                {
                    var duplicate = JsonRpcResponse.Failure(request.Id,
                        JsonRpcError.InvalidRequest("Server is already initialized."));
                    McpDiagnostics.SetError(activity, errorCode: duplicate.Error?.Code);
                    return duplicate;
                }
            }
            else if (request.Method != McpMethods.Ping && !_initialized)
            {
                var notReady = JsonRpcResponse.Failure(request.Id,
                    JsonRpcError.InvalidRequest($"Received '{request.Method}' before initialization was complete."));
                McpDiagnostics.SetError(activity, errorCode: notReady.Error?.Code);
                return notReady;
            }

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
        catch (JsonException ex)
        {
            // Malformed or mistyped request params are a caller error, not an internal one.
            McpDiagnostics.SetError(activity, ex);
            return JsonRpcResponse.Failure(request.Id, JsonRpcError.InvalidParams(ex.Message));
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

    #region Server-Initiated Requests

    private static string? TryGetStringProperty(JsonElement? @params, string name) =>
        @params is { } p && p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private RequestId NextId() => (RequestId)Interlocked.Increment(ref _nextId);

    /// <summary>
    /// Send a request from the server to the client and await the correlated response. Outbound
    /// params are serialized for the negotiated revision. Server-initiated request IDs are tracked
    /// independently of client-initiated ones, so overlapping numeric IDs never cross-correlate.
    /// </summary>
    private async Task<T> SendRequestAsync<T>(string method, object? @params, CancellationToken ct)
    {
        var id = NextId();
        var request = new JsonRpcRequest
        {
            Id = id,
            Method = method,
            Params = @params is not null ? ToWire(@params) : null
        };

        var pending = _tracker.Track(id, _options.RequestTimeout);
        try
        {
            await _transport.SendAsync(request, ct);
            var response = await pending.Task.WaitAsync(ct);

            if (response.IsError)
                throw new McpException(response.Error!.Code, response.Error.Message, response.Error.Data);

            return response.Result is null
                ? default!
                : JsonSerializer.Deserialize<T>(response.Result.Value, McpJsonDefaults.Options)!;
        }
        finally
        {
            pending.Dispose();
        }
    }

    /// <summary>Ping the client and wait for its acknowledgement.</summary>
    public async Task PingClientAsync(CancellationToken cancellationToken = default) =>
        await SendRequestAsync<JsonElement>(McpMethods.Ping, null, cancellationToken);

    /// <summary>Request the client's roots. Requires the client to have declared the roots capability.</summary>
    public async Task<IReadOnlyList<Root>> ListRootsAsync(CancellationToken cancellationToken = default)
    {
        _session.RequireClientCapability("roots");
        var result = await SendRequestAsync<ListRootsResult>(McpMethods.RootsList, null, cancellationToken);
        return result.Roots;
    }

    /// <summary>Ask the client to sample an LLM completion. Requires the client's sampling capability.</summary>
    public Task<CreateMessageResult> CreateMessageAsync(CreateMessageRequest request, CancellationToken cancellationToken = default)
    {
        _session.RequireClientCapability("sampling");
        return SendRequestAsync<CreateMessageResult>(McpMethods.SamplingCreateMessage, request, cancellationToken);
    }

    /// <summary>Ask the client to elicit input from the user. Requires the client's elicitation capability.</summary>
    public Task<ElicitResult> ElicitAsync(ElicitRequest request, CancellationToken cancellationToken = default)
    {
        _session.RequireClientCapability("elicitation");
        return SendRequestAsync<ElicitResult>(McpMethods.ElicitationCreate, request, cancellationToken);
    }

    #endregion

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

        return JsonRpcResponse.Success(request.Id, ToWire(result));
    }

    private JsonRpcResponse HandleToolsList(JsonRpcRequest request)
    {
        var paginatedReq = request.GetParams<PaginatedRequest>() ?? new PaginatedRequest();
        var allTools = _tools.Values.Select(h => h.Tool).ToList();
        var page = _pagination.GetPage(allTools, paginatedReq.Cursor);

        var result = new { tools = page.Items, nextCursor = page.NextCursor };
        return JsonRpcResponse.Success(request.Id, ToWire(result));
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
            return JsonRpcResponse.Success(request.Id, ToWire(result));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var errorResult = CallToolResult.Error(ex.Message);
            return JsonRpcResponse.Success(request.Id, ToWire(errorResult));
        }
    }

    private JsonRpcResponse HandleResourcesList(JsonRpcRequest request)
    {
        var paginatedReq = request.GetParams<PaginatedRequest>() ?? new PaginatedRequest();
        var allResources = _resources.Values.Select(h => h.Resource).ToList();
        var page = _pagination.GetPage(allResources, paginatedReq.Cursor);

        var result = new { resources = page.Items, nextCursor = page.NextCursor };
        return JsonRpcResponse.Success(request.Id, ToWire(result));
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
        return JsonRpcResponse.Success(request.Id, ToWire(result));
    }

    private JsonRpcResponse HandleResourcesTemplatesList(JsonRpcRequest request)
    {
        var paginatedReq = request.GetParams<PaginatedRequest>() ?? new PaginatedRequest();
        var page = _pagination.GetPage(_resourceTemplates, paginatedReq.Cursor);

        var result = new { resourceTemplates = page.Items, nextCursor = page.NextCursor };
        return JsonRpcResponse.Success(request.Id, ToWire(result));
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
        return JsonRpcResponse.Success(request.Id, ToWire(result));
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
        return JsonRpcResponse.Success(request.Id, ToWire(result));
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
            return JsonRpcResponse.Success(request.Id, ToWire(result));
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
        return JsonRpcResponse.Success(request.Id, ToWire(completionResult));
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
                _initialized = true;
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
        Tools = _tools.Count > 0
            ? new ListChangedCapability { ListChanged = _options.ToolsListChanged }
            : null,
        // Advertised whenever resources OR resource templates are registered.
        Resources = _resources.Count > 0 || _resourceTemplates.Count > 0
            ? new ResourcesCapability { Subscribe = _options.ResourcesSubscribe, ListChanged = _options.ResourcesListChanged }
            : null,
        Prompts = _prompts.Count > 0
            ? new ListChangedCapability { ListChanged = _options.PromptsListChanged }
            : null,
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
        // Deterministically fail any in-flight server-initiated requests.
        _tracker.CancelAll("Server disposing");
        _tracker.Dispose();
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

    /// <summary>Timeout for server-initiated requests. Null means no timeout.</summary>
    public TimeSpan? RequestTimeout { get; init; }

    /// <summary>Advertise tools/list_changed support when tools are registered.</summary>
    public bool ToolsListChanged { get; init; } = true;

    /// <summary>Advertise resource subscription support when resources are registered.</summary>
    public bool ResourcesSubscribe { get; init; } = true;

    /// <summary>Advertise resources/list_changed support when resources are registered.</summary>
    public bool ResourcesListChanged { get; init; } = true;

    /// <summary>Advertise prompts/list_changed support when prompts are registered.</summary>
    public bool PromptsListChanged { get; init; } = true;
}
