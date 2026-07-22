using System.Collections.Concurrent;
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
    private readonly List<ResourceTemplateHandler> _templateHandlers = new();
    private readonly Dictionary<string, PromptHandler> _prompts = new();
    private readonly List<CompletionRegistration> _completions = new();
    private readonly ResourceSubscriptionManager _subscriptions = new();
    private readonly PaginationHelper _pagination;
    private readonly ITaskStore _taskStore;
    private readonly PendingRequestTracker _tracker = new();
    // Cancellation sources for inbound requests currently being handled, keyed by request id.
    private readonly ConcurrentDictionary<RequestId, CancellationTokenSource> _inflight = new();
    // Serializes outbound transport writes across concurrently-completing handlers.
    private readonly SemaphoreSlim _writeLock = new(1, 1);
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
        _taskStore = _options.TaskStore ?? new InMemoryTaskStore();
    }

    #region Registration

    public McpServer AddTool(string name, string description, JsonElement inputSchema,
        Func<JsonElement?, CancellationToken, Task<CallToolResult>> handler,
        ToolAnnotations? annotations = null, JsonElement? outputSchema = null) =>
        AddTool(name, description, inputSchema, (args, _, ct) => handler(args, ct), annotations, outputSchema);

    /// <summary>
    /// Register a tool whose handler receives an <see cref="IProgress{McpProgress}"/> for reporting
    /// progress. Reported progress is forwarded to the client only when the request carried a
    /// progress token in its _meta; otherwise it is discarded. When <paramref name="outputSchema"/>
    /// is supplied, the result's structuredContent is validated against it.
    /// </summary>
    public McpServer AddTool(string name, string description, JsonElement inputSchema,
        Func<JsonElement?, IProgress<McpProgress>, CancellationToken, Task<CallToolResult>> handler,
        ToolAnnotations? annotations = null, JsonElement? outputSchema = null)
    {
        // Reject a malformed input/output schema at registration rather than at call time.
        var schemaErrors = JsonSchemaValidator.ValidateSchema(inputSchema);
        if (outputSchema is { } os)
            schemaErrors = schemaErrors.Concat(JsonSchemaValidator.ValidateSchema(os)).ToList();
        if (schemaErrors.Count > 0)
            throw new ArgumentException($"Invalid schema for tool '{name}': {string.Join("; ", schemaErrors)}");

        _tools[name] = new ToolHandler
        {
            Tool = new Tool
            {
                Name = name,
                Description = description,
                InputSchema = inputSchema,
                OutputSchema = outputSchema,
                Annotations = annotations
            },
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

    /// <summary>Register a schemaless tool whose handler receives an <see cref="IProgress{McpProgress}"/>.</summary>
    public McpServer AddTool(string name, string description,
        Func<JsonElement?, IProgress<McpProgress>, CancellationToken, Task<CallToolResult>> handler)
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

    /// <summary>
    /// Register a resource template with a handler. A resources/read for a URI matching the
    /// template resolves its variables and invokes the handler, which may return multiple contents.
    /// </summary>
    public McpServer AddResourceTemplate(string uriTemplate, string name,
        Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task<IReadOnlyList<ResourceContents>>> handler,
        string? description = null, string? mimeType = null)
    {
        AddResourceTemplate(uriTemplate, name, description, mimeType);
        _templateHandlers.Add(new ResourceTemplateHandler
        {
            Matcher = new UriTemplate(uriTemplate),
            Handler = handler
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
            case JsonRpcRequest { Method: McpMethods.Initialize } initialize:
                // Handle the lifecycle-critical initialize in order, before any later message.
                await HandleAndSendAsync(initialize, ct);
                break;

            case JsonRpcRequest request:
                // Dispatch independent requests concurrently so a slow handler never blocks
                // fast ones and the loop stays free to process cancellations.
                DispatchRequest(request, ct);
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

    private void DispatchRequest(JsonRpcRequest request, CancellationToken loopCt)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(loopCt);
        _inflight[request.Id] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                var response = await HandleRequestAsync(request, cts.Token);
                // A request cancelled via notifications/cancelled gets no response.
                if (!cts.IsCancellationRequested)
                    await SendMessageAsync(response, loopCt);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unhandled error dispatching {Method}", request.Method);
            }
            finally
            {
                if (_inflight.TryRemove(request.Id, out var removed))
                    removed.Dispose();
            }
        }, CancellationToken.None);
    }

    private async Task HandleAndSendAsync(JsonRpcRequest request, CancellationToken ct)
    {
        var response = await HandleRequestAsync(request, ct);
        await SendMessageAsync(response, ct);
    }

    /// <summary>Serializes outbound writes so concurrent handlers never interleave on the transport.</summary>
    private async Task SendMessageAsync(JsonRpcMessage message, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await _transport.SendAsync(message, ct);
        }
        finally
        {
            _writeLock.Release();
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
                McpMethods.TasksGet => HandleTasksGet(request),
                McpMethods.TasksList => HandleTasksList(request),
                McpMethods.TasksResult => HandleTasksResult(request),
                McpMethods.TasksCancel => HandleTasksCancel(request),
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
            await SendMessageAsync(request, ct);
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

        var reporter = CreateProgressReporter(callReq.Meta);

        // Task augmentation (experimental): run in the background and return a CreateTaskResult
        // immediately; the real result is retrieved later via tasks/result.
        if (TryGetTaskMetadata(request.Params, out var taskMetadata))
        {
            var task = _taskStore.Create(taskMetadata, TaskOwnerKey);
            RunToolAsTask(task.TaskId, handler, callReq.Arguments, reporter);
            return JsonRpcResponse.Success(request.Id, ToWire(new CreateTaskResult { Task = task }));
        }

        try
        {
            var result = await handler.Handler(callReq.Arguments, reporter, ct);

            var outputError = ValidateToolOutput(handler, result);
            if (outputError is not null)
                return JsonRpcResponse.Failure(request.Id, JsonRpcError.InternalError(outputError));

            return JsonRpcResponse.Success(request.Id, ToWire(result));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var errorResult = CallToolResult.Error(ex.Message);
            return JsonRpcResponse.Success(request.Id, ToWire(errorResult));
        }
    }

    /// <summary>Owner key that scopes tasks for this server connection.</summary>
    /// <summary>
    /// Owner key scoping this server's tasks in the (possibly shared/durable) task store. When
    /// multiple connections share a store, set a distinct key per session/principal so one session
    /// cannot list, inspect, cancel, or retrieve another's tasks.
    /// </summary>
    private string? TaskOwnerKey => _options.TaskOwnerKey;

    private static bool TryGetTaskMetadata(JsonElement? @params, out TaskMetadata? metadata)
    {
        metadata = null;
        if (@params is { } p && p.TryGetProperty("task", out var task) && task.ValueKind == JsonValueKind.Object)
        {
            metadata = task.Deserialize<TaskMetadata>(McpJsonDefaults.Options) ?? new TaskMetadata();
            return true;
        }
        return false;
    }

    private static string? ValidateToolOutput(ToolHandler handler, CallToolResult result)
    {
        if (handler.Tool.OutputSchema is { } outputSchema && result.StructuredContent is { } structured)
        {
            var errors = JsonSchemaValidator.Validate(structured, outputSchema);
            if (errors.Count > 0)
                return $"Tool output did not conform to its output schema: {string.Join("; ", errors)}";
        }
        return null;
    }

    private void RunToolAsTask(string taskId,
        ToolHandler handler, JsonElement? arguments, IProgress<McpProgress> reporter)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await handler.Handler(arguments, reporter, _cts?.Token ?? CancellationToken.None);
                var outputError = ValidateToolOutput(handler, result);
                if (outputError is not null)
                    _taskStore.SetFailed(taskId, outputError);
                else
                    _taskStore.SetResult(taskId, McpJsonDefaults.ToElement(result));
            }
            catch (Exception ex)
            {
                _taskStore.SetFailed(taskId, ex.Message);
            }
        }, CancellationToken.None);
    }

    private JsonRpcResponse HandleTasksGet(JsonRpcRequest request)
    {
        var taskId = request.GetParams<TaskIdParams>()!.TaskId;
        var task = _taskStore.Get(taskId, TaskOwnerKey);
        return task is null
            ? JsonRpcResponse.Failure(request.Id, JsonRpcError.InvalidParams($"Unknown task: '{taskId}'"))
            : JsonRpcResponse.Success(request.Id, ToWire(task));
    }

    private JsonRpcResponse HandleTasksList(JsonRpcRequest request) =>
        JsonRpcResponse.Success(request.Id, ToWire(new ListTasksResult { Tasks = _taskStore.List(TaskOwnerKey) }));

    private JsonRpcResponse HandleTasksResult(JsonRpcRequest request)
    {
        var taskId = request.GetParams<TaskIdParams>()!.TaskId;
        var task = _taskStore.Get(taskId, TaskOwnerKey);
        if (task is null)
            return JsonRpcResponse.Failure(request.Id, JsonRpcError.InvalidParams($"Unknown task: '{taskId}'"));
        if (task.Status != McpTaskStatus.Completed)
            return JsonRpcResponse.Failure(request.Id,
                JsonRpcError.InvalidRequest($"Task '{taskId}' result is not available (status: {task.Status})."));

        var payload = _taskStore.GetResult(taskId, TaskOwnerKey);
        return JsonRpcResponse.Success(request.Id, payload ?? McpJsonDefaults.ToElement(new { }));
    }

    private JsonRpcResponse HandleTasksCancel(JsonRpcRequest request)
    {
        var taskId = request.GetParams<TaskIdParams>()!.TaskId;
        var task = _taskStore.Cancel(taskId, TaskOwnerKey);
        return task is null
            ? JsonRpcResponse.Failure(request.Id, JsonRpcError.InvalidParams($"Unknown task: '{taskId}'"))
            : JsonRpcResponse.Success(request.Id, ToWire(task));
    }

    /// <summary>
    /// Build an <see cref="IProgress{McpProgress}"/> that forwards to the client via
    /// notifications/progress when the request carried a <c>_meta.progressToken</c>, or a no-op
    /// reporter otherwise.
    /// </summary>
    private IProgress<McpProgress> CreateProgressReporter(JsonElement? meta)
    {
        if (meta is { } m && m.TryGetProperty("progressToken", out var token))
        {
            RequestId? id = token.ValueKind switch
            {
                JsonValueKind.String => (RequestId)token.GetString()!,
                JsonValueKind.Number => (RequestId)token.GetInt64(),
                _ => null
            };
            if (id is { } progressToken)
                return new ServerProgress(this, progressToken);
        }
        return NoOpProgress.Instance;
    }

    private async Task SendProgressAsync(RequestId progressToken, McpProgress progress)
    {
        try
        {
            await SendMessageAsync(new JsonRpcNotification
            {
                Method = McpMethods.NotificationsProgress,
                Params = McpJsonDefaults.ToElement(new ProgressParams
                {
                    ProgressToken = progressToken,
                    Progress = progress.Progress,
                    Total = progress.Total,
                    Message = progress.Message
                })
            });
        }
        catch
        {
            // Best-effort: progress delivery must never fault the handler.
        }
    }

    private sealed class ServerProgress : IProgress<McpProgress>
    {
        private readonly McpServer _server;
        private readonly RequestId _token;
        private readonly object _gate = new();
        private double _last = double.NegativeInfinity;

        public ServerProgress(McpServer server, RequestId token)
        {
            _server = server;
            _token = token;
        }

        public void Report(McpProgress value)
        {
            lock (_gate)
            {
                // Progress MUST increase monotonically; drop out-of-order reports.
                if (value.Progress <= _last)
                    return;
                _last = value.Progress;
            }
            _ = _server.SendProgressAsync(_token, value);
        }
    }

    private sealed class NoOpProgress : IProgress<McpProgress>
    {
        public static readonly NoOpProgress Instance = new();
        public void Report(McpProgress value) { }
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

        if (_resources.TryGetValue(uri, out var handler))
        {
            var contents = await handler.Handler(uri, ct);
            return JsonRpcResponse.Success(request.Id, ToWire(new ReadResourceResult { Contents = [contents] }));
        }

        // No static resource — try resource templates (first match wins).
        foreach (var template in _templateHandlers)
        {
            if (template.Matcher.TryMatch(uri, out var variables))
            {
                var contents = await template.Handler(uri, variables, ct);
                return JsonRpcResponse.Success(request.Id, ToWire(new ReadResourceResult { Contents = contents }));
            }
        }

        return JsonRpcResponse.Failure(request.Id, JsonRpcError.ResourceNotFound($"Resource not found: '{uri}'"));
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
                var cancelled = notification.GetParams<CancelledParams>();
                if (cancelled is not null && _inflight.TryGetValue(cancelled.RequestId, out var inflightCts))
                {
                    _logger.LogDebug("Cancelling request {Id}: {Reason}", cancelled.RequestId, cancelled.Reason);
                    inflightCts.Cancel();
                }
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
        await SendMessageAsync(new JsonRpcNotification { Method = McpMethods.NotificationsToolsListChanged });
    }

    public async Task NotifyResourcesChangedAsync()
    {
        await SendMessageAsync(new JsonRpcNotification { Method = McpMethods.NotificationsResourcesListChanged });
    }

    public async Task NotifyResourceUpdatedAsync(string uri)
    {
        if (_subscriptions.HasSubscribers(uri))
        {
            await SendMessageAsync(new JsonRpcNotification
            {
                Method = McpMethods.NotificationsResourcesUpdated,
                Params = McpJsonDefaults.ToElement(new { uri })
            });
        }
    }

    public async Task NotifyPromptsChangedAsync()
    {
        await SendMessageAsync(new JsonRpcNotification { Method = McpMethods.NotificationsPromptsListChanged });
    }

    /// <summary>
    /// Send a log message to the client. Filtered by the client's set log level.
    /// </summary>
    public async Task LogAsync(McpLogLevel level, JsonElement data, string? loggerName = null)
    {
        if (!_loggingEnabled) return;
        if ((int)level > (int)_logLevel) return; // Filter: only send at or above set level

        await SendMessageAsync(new JsonRpcNotification
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
        // Deterministically cancel/dispose in-flight inbound handlers and pending outbound requests.
        foreach (var kvp in _inflight)
            kvp.Value.Cancel();
        foreach (var kvp in _inflight)
            kvp.Value.Dispose();
        _inflight.Clear();
        _tracker.CancelAll("Server disposing");
        _tracker.Dispose();
        _writeLock.Dispose();
        await _transport.DisposeAsync();
        _cts?.Dispose();
    }

    #region Internal types

    private sealed class ToolHandler
    {
        public required Tool Tool { get; init; }
        public required Func<JsonElement?, IProgress<McpProgress>, CancellationToken, Task<CallToolResult>> Handler { get; init; }
    }

    private sealed class ResourceHandler
    {
        public required Resource Resource { get; init; }
        public required Func<string, CancellationToken, Task<ResourceContents>> Handler { get; init; }
    }

    private sealed class ResourceTemplateHandler
    {
        public required UriTemplate Matcher { get; init; }
        public required Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task<IReadOnlyList<ResourceContents>>> Handler { get; init; }
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

    /// <summary>
    /// Store backing experimental task execution. Defaults to an in-memory store. Share one store
    /// across connections (with a distinct <see cref="TaskOwnerKey"/> each) for durable tasks.
    /// </summary>
    public ITaskStore? TaskStore { get; init; }

    /// <summary>
    /// Owner key that scopes this server's tasks in a shared/durable store. Null keeps tasks owned
    /// by the (single) connection.
    /// </summary>
    public string? TaskOwnerKey { get; init; }

    /// <summary>Advertise tools/list_changed support when tools are registered.</summary>
    public bool ToolsListChanged { get; init; } = true;

    /// <summary>Advertise resource subscription support when resources are registered.</summary>
    public bool ResourcesSubscribe { get; init; } = true;

    /// <summary>Advertise resources/list_changed support when resources are registered.</summary>
    public bool ResourcesListChanged { get; init; } = true;

    /// <summary>Advertise prompts/list_changed support when prompts are registered.</summary>
    public bool PromptsListChanged { get; init; } = true;
}
