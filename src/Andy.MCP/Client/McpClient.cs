using System.Runtime.CompilerServices;
using System.Text.Json;
using Andy.MCP.Protocol;
using Andy.MCP.Server;
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
    /// <summary>Hard deadline that progress cannot extend. Null disables the hard deadline.</summary>
    public TimeSpan? MaximumRequestDuration { get; init; } = TimeSpan.FromMinutes(5);
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    /// <summary>Experimental task store. Defaults to an in-memory store.</summary>
    public ITaskStore? TaskStore { get; init; }
    /// <summary>Enable experimental task reception for configured handlers and stores.</summary>
    public bool EnableExperimentalTasks { get; init; } = true;
    /// <summary>Trusted ownership scope for retained tasks. Null isolates each connection/session.</summary>
    public string? TaskOwnerKey { get; init; }
    /// <summary>Extension request handlers copied when the client is created.</summary>
    public IReadOnlyDictionary<string, Func<JsonElement?, CancellationToken, Task<JsonElement>>> CustomRequestHandlers { get; init; } =
        new Dictionary<string, Func<JsonElement?, CancellationToken, Task<JsonElement>>>();

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
    private ClientTasksCapability? BuildTaskCapabilities()
    {
        if (!EnableExperimentalTasks || (SamplingHandler is null && ElicitationHandler is null && TaskStore is null)) return null;
        var configured = Capabilities.Tasks;
        return new ClientTasksCapability
        {
            List = configured is null ? new() : configured.List,
            Cancel = configured is null ? new() : configured.Cancel,
            Requests = new ClientTaskRequests
            {
                Sampling = SamplingHandler is null ? null : configured is null ? new SamplingTaskRequests { CreateMessage = new() } : configured.Requests?.Sampling,
                Elicitation = ElicitationHandler is null ? null : configured is null ? new ElicitationTaskRequests { Create = new() } : configured.Requests?.Elicitation
            },
            ExtensionData = configured?.ExtensionData
        };
    }

    internal ClientCapabilities BuildCapabilities()
    {
        return new ClientCapabilities
        {
            Roots = RootProvider is not null
                ? Capabilities.Roots ?? new RootsCapability { ListChanged = true }
                : null,
            Sampling = SamplingHandler is not null
                ? Capabilities.Sampling ?? new SamplingCapability()
                : null,
            Elicitation = ElicitationHandler is not null
                ? Capabilities.Elicitation ?? new ElicitationCapability { Form = new EmptyCapability() }
                : null,
            Tasks = BuildTaskCapabilities(),
            Extensions = Capabilities.Extensions,
            ExtensionData = Capabilities.ExtensionData,
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
    private InboundRequestRegistry _inbound = new();
    private InboundRequestRegistry _background = new();
    private readonly List<Task> _retiredHandlers = new();
    private readonly ITaskStore _taskStore;
    private string _taskOwnerKey;
    private readonly PaginationHelper _taskPagination = new(Guid.NewGuid().ToString("N"));
    private long _nextId;
    private Task? _messageLoop;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private EventHandler? _rootsChangedHandler;
    private readonly Dictionary<string, Func<JsonElement?, CancellationToken, Task<JsonElement>>> _customHandlers;

    /// <summary>
    /// The negotiated session state and capabilities.
    /// </summary>
    public McpSession Session => _session;

    /// <summary>Test-only: number of outbound requests still awaiting a response.</summary>
    internal int PendingRequestCount => _tracker.Count;

    // Events for server notifications
    public event EventHandler<ElicitationCompleteParams>? ElicitationCompleted;
    public event EventHandler<JsonRpcNotification>? CustomNotificationReceived;
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
        _taskStore = options.TaskStore ?? new InMemoryTaskStore();
        _taskOwnerKey = options.TaskOwnerKey ?? Guid.NewGuid().ToString("N");
        _customHandlers = new(options.CustomRequestHandlers, StringComparer.Ordinal);
        foreach (var method in _customHandlers.Keys) ExtensionMethods.RequireCustom(method);
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
        if (_transport is StreamableHttpClientTransport http) http.SessionReinitialized += OnSessionReinitialized;

        _session.Transition(McpSessionState.Initializing);

        // Start message processing loop
        _messageLoop = Task.Run(() => MessageLoopAsync(_cts.Token), _cts.Token);

        // Wire up root provider change notifications
        if (_options.RootProvider is not null)
        {
            _rootsChangedHandler = async (_, _) =>
            {
                if (_session.State != McpSessionState.Ready || _options.BuildCapabilities().Roots?.ListChanged != true) return;
                try { await NotifyRootsChangedAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to send roots/list_changed"); }
            };
            _options.RootProvider.RootsChanged += _rootsChangedHandler;
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

    public Task<ReadResourceResult> ReadResourceAsync(ReadResourceRequestParams request, McpRequestOptions? options = null, CancellationToken ct = default)
    {
        RequireReady(); _session.RequireServerCapability("resources");
        return SendRequestAsync<ReadResourceResult>(McpMethods.ResourcesRead, request, ct, requestOptions: options);
    }
    public Task<GetPromptResult> GetPromptAsync(GetPromptRequestParams request, McpRequestOptions? options = null, CancellationToken ct = default)
    {
        RequireReady(); _session.RequireServerCapability("prompts");
        return SendRequestAsync<GetPromptResult>(McpMethods.PromptsGet, request, ct, requestOptions: options);
    }
    public Task SetLogLevelAsync(SetLogLevelParams request, McpRequestOptions? options = null, CancellationToken ct = default)
    {
        RequireReady(); _session.RequireServerCapability("logging");
        return SendRequestAsync<JsonElement>(McpMethods.LoggingSetLevel, request, ct, requestOptions: options);
    }
    public Task SubscribeResourceAsync(SubscribeRequestParams request, McpRequestOptions? options = null, CancellationToken ct = default)
    {
        RequireReady(); RequireResourceSubscription();
        return SendRequestAsync<JsonElement>(McpMethods.ResourcesSubscribe, request, ct, requestOptions: options);
    }
    public Task UnsubscribeResourceAsync(UnsubscribeRequestParams request, McpRequestOptions? options = null, CancellationToken ct = default)
    {
        RequireReady(); RequireResourceSubscription();
        return SendRequestAsync<JsonElement>(McpMethods.ResourcesUnsubscribe, request, ct, requestOptions: options);
    }


    /// <summary>Notify the server of root changes only when listChanged was advertised.</summary>
    public Task NotifyRootsChangedAsync(CancellationToken ct = default)
    {
        if (_session.State != McpSessionState.Ready) throw new McpSessionException("The MCP session is not ready.");
        if (_options.BuildCapabilities().Roots?.ListChanged != true) throw new McpCapabilityNotAvailableException("roots.listChanged");
        return _transport.SendAsync(new JsonRpcNotification { Method = McpMethods.NotificationsRootsListChanged }, ct);
    }

    /// <summary>Send an extension request through normal correlation, cancellation and deadline tracking.</summary>
    public Task<T> RequestCustomAsync<T>(string method, object? parameters = null, McpRequestOptions? options = null, CancellationToken ct = default)
    {
        RequireReady();
        ExtensionMethods.RequireCustom(method);
        return SendRequestAsync<T>(method, parameters, ct, requestOptions: options);
    }

    public Task NotifyCustomAsync(string method, object? parameters = null, CancellationToken ct = default)
    {
        RequireReady();
        ExtensionMethods.RequireCustom(method);
        return _transport.SendAsync(new JsonRpcNotification { Method = method, Params = ExtensionMethods.ObjectParameters(parameters is null ? null : ToWire(parameters)) }, ct);
    }

    private void RequireReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session.State != McpSessionState.Ready) throw new McpSessionException("The MCP session is not ready.");
    }

    public Task<ToolsListResult> ListToolsPageAsync(PaginatedRequest? request = null, McpRequestOptions? options = null, CancellationToken ct = default)
    {
        RequireReady(); _session.RequireServerCapability("tools");
        return SendRequestAsync<ToolsListResult>(McpMethods.ToolsList, request, ct, requestOptions: options);
    }
    public Task<ResourcesListResult> ListResourcesPageAsync(PaginatedRequest? request = null, McpRequestOptions? options = null, CancellationToken ct = default)
    {
        RequireReady(); _session.RequireServerCapability("resources");
        return SendRequestAsync<ResourcesListResult>(McpMethods.ResourcesList, request, ct, requestOptions: options);
    }
    public Task<ResourceTemplatesListResult> ListResourceTemplatesPageAsync(PaginatedRequest? request = null, McpRequestOptions? options = null, CancellationToken ct = default)
    {
        RequireReady(); _session.RequireServerCapability("resources");
        return SendRequestAsync<ResourceTemplatesListResult>(McpMethods.ResourcesTemplatesList, request, ct, requestOptions: options);
    }
    public Task<PromptsListResult> ListPromptsPageAsync(PaginatedRequest? request = null, McpRequestOptions? options = null, CancellationToken ct = default)
    {
        RequireReady(); _session.RequireServerCapability("prompts");
        return SendRequestAsync<PromptsListResult>(McpMethods.PromptsList, request, ct, requestOptions: options);
    }
    public Task<CallToolResult> CallToolAsync(CallToolRequest request, McpRequestOptions? options = null, CancellationToken ct = default)
    {
        RequireReady(); _session.RequireServerCapability("tools");
        if (request.Task is not null) throw new ArgumentException("Use CallToolAsTaskAsync for task augmentation.", nameof(request));
        return SendRequestAsync<CallToolResult>(McpMethods.ToolsCall, request, ct, requestOptions: options);
    }

    public Task PingAsync(McpRequestOptions options, CancellationToken ct = default)
    {
        RequireReady(); return SendRequestAsync<JsonElement>(McpMethods.Ping, null, ct, requestOptions: options);
    }
    public Task<ReadResourceResult> ReadResourceAsync(string uri, McpRequestOptions options, CancellationToken ct = default)
    {
        RequireReady(); _session.RequireServerCapability("resources");
        return SendRequestAsync<ReadResourceResult>(McpMethods.ResourcesRead, new ResourceRequestParams { Uri = uri }, ct, requestOptions: options);
    }
    public Task<GetPromptResult> GetPromptAsync(string name, IDictionary<string, string>? arguments, McpRequestOptions options, CancellationToken ct = default)
    {
        RequireReady(); _session.RequireServerCapability("prompts");
        return SendRequestAsync<GetPromptResult>(McpMethods.PromptsGet, new GetPromptRequestParams { Name = name, Arguments = arguments }, ct, requestOptions: options);
    }
    public Task<CompletionResult> CompleteAsync(CompletionRequest request, McpRequestOptions options, CancellationToken ct = default)
    {
        RequireReady(); _session.RequireServerCapability("completions");
        return SendRequestAsync<CompletionResult>(McpMethods.CompletionComplete, request, ct, requestOptions: options);
    }
    public Task SubscribeResourceAsync(string uri, McpRequestOptions options, CancellationToken ct = default)
    {
        RequireReady(); RequireResourceSubscription();
        return SendRequestAsync<JsonElement>(McpMethods.ResourcesSubscribe, new ResourceRequestParams { Uri = uri }, ct, requestOptions: options);
    }
    public Task UnsubscribeResourceAsync(string uri, McpRequestOptions options, CancellationToken ct = default)
    {
        RequireReady(); RequireResourceSubscription();
        return SendRequestAsync<JsonElement>(McpMethods.ResourcesUnsubscribe, new ResourceRequestParams { Uri = uri }, ct, requestOptions: options);
    }
    public Task SetLogLevelAsync(string level, McpRequestOptions options, CancellationToken ct = default)
    {
        RequireReady(); _session.RequireServerCapability("logging");
        return SendRequestAsync<JsonElement>(McpMethods.LoggingSetLevel, new SetLogLevelParams { Level = JsonSerializer.Deserialize<McpLogLevel>(JsonSerializer.Serialize(level), McpJsonDefaults.Options) }, ct, requestOptions: options);
    }

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

    public Task<CallToolResult> CallToolAsync(string name, object? arguments = null, CancellationToken ct = default) =>
        CallToolAsync(name, arguments, progress: null, ct);

    /// <summary>
    /// Call a tool, receiving progress notifications. When <paramref name="progress"/> is provided,
    /// a progress token is attached to the request and matching notifications/progress are routed to it.
    /// </summary>
    public async Task<CallToolResult> CallToolAsync(string name, object? arguments,
        IProgress<McpProgress>? progress, CancellationToken ct = default)
    {
        _session.RequireServerCapability("tools");
        var args = arguments is JsonElement je
            ? je
            : arguments is not null
                ? McpJsonDefaults.ToElement(arguments)
                : (JsonElement?)null;

        RequestId? progressToken = null;
        JsonElement? meta = null;
        if (progress is not null)
        {
            progressToken = (RequestId)Guid.NewGuid().ToString("N");
            meta = McpJsonDefaults.ToElement(new Meta { ProgressToken = progressToken });
        }

        var request = new CallToolRequest
        {
            Name = name,
            Arguments = args,
            Meta = meta
        };

        return await SendRequestAsync<CallToolResult>(McpMethods.ToolsCall, request, ct, progress, progressToken);
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
            new ResourceRequestParams { Uri = uri }, ct);
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
            new GetPromptRequestParams { Name = name, Arguments = arguments }, ct);
    }

    public async Task SetLogLevelAsync(string level, CancellationToken ct = default)
    {
        _session.RequireServerCapability("logging");
        await SendRequestAsync<JsonElement>(McpMethods.LoggingSetLevel, new SetLogLevelParams { Level = JsonSerializer.Deserialize<McpLogLevel>(JsonSerializer.Serialize(level), McpJsonDefaults.Options) }, ct);
    }

    /// <summary>Request argument completions. Requires the server's completions capability.</summary>
    public async Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
    {
        _session.RequireServerCapability("completions");
        return await SendRequestAsync<CompletionResult>(McpMethods.CompletionComplete, request, ct);
    }

    /// <summary>
    /// Subscribe to updates for a resource. Requires the server to declare the resources
    /// subscribe sub-capability.
    /// </summary>
    public async Task SubscribeResourceAsync(string uri, CancellationToken ct = default)
    {
        RequireResourceSubscription();
        await SendRequestAsync<JsonElement>(McpMethods.ResourcesSubscribe, new ResourceRequestParams { Uri = uri }, ct);
    }

    /// <summary>Unsubscribe from updates for a resource.</summary>
    public async Task UnsubscribeResourceAsync(string uri, CancellationToken ct = default)
    {
        RequireResourceSubscription();
        await SendRequestAsync<JsonElement>(McpMethods.ResourcesUnsubscribe, new ResourceRequestParams { Uri = uri }, ct);
    }

    private void RequireResourceSubscription()
    {
        if (_session.ServerCapabilities?.Resources?.Subscribe != true)
            throw new McpCapabilityNotAvailableException("resources.subscribe");
    }

    // --- Experimental tasks (MCP 2025-11-25) ---

    /// <summary>
    /// Experimental: call a tool as a task. Returns immediately with the created task; retrieve the
    /// real result later with <see cref="GetTaskResultAsync"/>.
    /// </summary>
    public async Task<CreateTaskResult> CallToolAsTaskAsync(string name, object? arguments = null,
        long? ttlMs = null, CancellationToken ct = default)
    {
        _session.RequireServerCapability("tools");
        RequireServerTask(McpMethods.ToolsCall);
        var descriptor = (await ListToolsAsync(ct)).FirstOrDefault(tool => tool.Name == name);
        if (descriptor?.Execution?.TaskSupport is not ("optional" or "required"))
            throw new McpCapabilityNotAvailableException($"task execution for tool '{name}'");

        var args = arguments is JsonElement je
            ? je
            : arguments is not null
                ? McpJsonDefaults.ToElement(arguments)
                : (JsonElement?)null;

        return await SendRequestAsync<CreateTaskResult>(McpMethods.ToolsCall,
            new { name, arguments = args, task = new TaskMetadata { Ttl = ttlMs } }, ct);
    }

    private void RequireServerTask(string method) =>
        TaskProtocol.Require(TaskProtocol.Supports(_session.ServerCapabilities?.Tasks, method), _session.Revision, method);

    /// <summary>Experimental: get a task's current state.</summary>
    public Task<McpTask> GetTaskAsync(string taskId, CancellationToken ct = default)
    {
        RequireServerTask(McpMethods.TasksGet);
        return SendRequestAsync<McpTask>(McpMethods.TasksGet, new TaskIdParams { TaskId = taskId }, ct);
    }

    /// <summary>Experimental: list all tasks owned by this caller, following opaque cursors.</summary>
    public async Task<IReadOnlyList<McpTask>> ListTasksAsync(CancellationToken ct = default)
    {
        var tasks = new List<McpTask>();
        string? cursor = null;
        do
        {
            var page = await ListTasksPageAsync(new PaginatedRequest { Cursor = cursor }, ct: ct);
            tasks.AddRange(page.Tasks);
            cursor = page.NextCursor;
        } while (cursor is not null);
        return tasks;
    }

    /// <summary>Experimental: retrieve one task page, retaining its cursor and metadata.</summary>
    public Task<ListTasksResult> ListTasksPageAsync(PaginatedRequest? request = null, McpRequestOptions? options = null, CancellationToken ct = default)
    {
        RequireServerTask(McpMethods.TasksList);
        return SendRequestAsync<ListTasksResult>(McpMethods.TasksList, request ?? new PaginatedRequest(), ct, requestOptions: options);
    }

    /// <summary>Experimental: wait for and retrieve a task's final result payload.</summary>
    public Task<JsonElement> GetTaskResultAsync(string taskId, CancellationToken ct = default)
    {
        RequireServerTask(McpMethods.TasksResult);
        return SendRequestAsync<JsonElement>(McpMethods.TasksResult, new TaskIdParams { TaskId = taskId }, ct);
    }

    /// <summary>Experimental: cancel a task.</summary>
    public Task<McpTask> CancelTaskAsync(string taskId, CancellationToken ct = default)
    {
        RequireServerTask(McpMethods.TasksCancel);
        return SendRequestAsync<McpTask>(McpMethods.TasksCancel, new TaskIdParams { TaskId = taskId }, ct);
    }

    #endregion

    #region Message Sending

    private JsonElement ToWire<T>(T value) =>
        RevisionAwareJson.ToElementForRevision(value, _session.Revision ?? ProtocolRevision.Latest);

    private RequestId NextId() => (RequestId)Interlocked.Increment(ref _nextId);

    private async Task<T> SendRequestAsync<T>(string method, object? @params, CancellationToken ct,
        IProgress<McpProgress>? progress = null, RequestId? progressToken = null, McpRequestOptions? requestOptions = null)
    {
        using var input = TaskExecutionContext.Current?.BeginInput();
        var id = NextId();
        using var activity = McpDiagnostics.StartClientRequest(
            method, id, _session.RemoteInfo?.Name, _session.ProtocolVersion);

        var request = new JsonRpcRequest
        {
            Id = id,
            Method = method,
            Params = TaskExecutionContext.AttachCurrent(@params is not null ? ToWire(@params) : null)
        };

        if (requestOptions?.Progress is { } observer)
        {
            progress = observer; progressToken ??= (RequestId)Guid.NewGuid().ToString("N");
            request = request with { Params = ExtensionMethods.WithProgress(request.Params, progressToken.Value) };
        }
        ExtensionMethods.ObjectParameters(request.Params);
        ProtocolShapeValidation.Request(request, _session.Revision ?? ProtocolRevision.Latest);
        var pending = _tracker.Track(id, requestOptions?.Timeout ?? _options.RequestTimeout,
            requestOptions?.MaximumDuration ?? _options.MaximumRequestDuration, _options.TimeProvider);
        if (progress is not null && progressToken is { } token)
        {
            pending.ProgressToken = token;
            pending.OnProgress(progress);
        }
        try
        {
            using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, pending.CancellationToken);
            try { await _transport.SendAsync(request, sendCancellation.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && pending.Task.IsCompleted)
            {
                // Preserve the tracked timeout error when it interrupted a blocking HTTP POST/write.
                await pending.Task;
                throw;
            }
            var response = await pending.Task.WaitAsync(ct);

            if (response.IsError)
            {
                var ex = new McpException(response.Error!.Code, response.Error.Message, response.Error.Data);
                McpDiagnostics.SetError(activity, ex, response.Error.Code);
                throw ex;
            }

            ProtocolShapeValidation.Result(request, response.Result, _session.Revision ?? ProtocolRevision.Latest);
            McpDiagnostics.SetSuccess(activity);

            if (response.Result is null)
                return default!;

            return JsonSerializer.Deserialize<T>(response.Result.Value, McpJsonDefaults.Options)!;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException
                                   && method != McpMethods.Initialize)
        {
            // Caller cancellation or request timeout stopped us waiting: tell the server to stop
            // handling this request. The initialize request MUST NOT be cancelled this way.
            await TrySendCancellationAsync(id);
            throw;
        }
        catch (Exception ex) when (activity is not null && ex is not McpException)
        {
            McpDiagnostics.SetError(activity, ex);
            throw;
        }
        finally
        {
            // Ensure the request leaves the tracker on every path (success removes it via
            // TryComplete; cancellation/timeout would otherwise leak an entry).
            _tracker.TryCancel(id);
            pending.Dispose();
        }
    }

    private async Task TrySendCancellationAsync(RequestId id)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await _transport.SendAsync(new JsonRpcNotification
            {
                Method = McpMethods.NotificationsCancelled,
                Params = McpJsonDefaults.ToElement(new CancelledParams { RequestId = id })
            }, timeout.Token);
        }
        catch
        {
            // Best-effort: the transport may already be gone.
        }
    }

    private async Task SendNotificationAsync(string method, object? @params = null)
    {
        var notification = new JsonRpcNotification
        {
            Method = method,
            Params = TaskExecutionContext.AttachCurrent(@params is not null ? ToWire(@params) : null)
        };
        ProtocolShapeValidation.Notification(notification, _session.Revision ?? ProtocolRevision.Latest);
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
            case JsonRpcUncorrelatedError error:
                _logger.LogWarning("Peer reported uncorrelated protocol error {Code}", error.Error.Code);
                break;
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
        ProtocolShapeValidation.Notification(notification, _session.Revision ?? ProtocolRevision.Latest);
        switch (notification.Method)
        {
            case McpMethods.NotificationsElicitationComplete:
                if (_session.Revision?.AtLeast(ProtocolRevision.V2025_11_25) == true && _options.BuildCapabilities().Elicitation?.Url is not null &&
                    notification.GetParams<ElicitationCompleteParams>() is { } completion)
                    ElicitationCompleted?.Invoke(this, completion);
                break;
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
            default:
                if (_session.State == McpSessionState.Ready) CustomNotificationReceived?.Invoke(this, notification);
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
        _inbound.Cancel(p.RequestId);
    }

    private void HandleServerRequest(JsonRpcRequest request)
    {
        if (!_inbound.Run(request.Id, _cts?.Token ?? CancellationToken.None, async ct =>
        {
            try
            {
                JsonRpcResponse response;
                try { response = await DispatchServerRequestAsync(request, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) when (ex is ArgumentException or JsonException or McpCapabilityNotAvailableException or McpPaginationException)
                {
                    response = JsonRpcResponse.Failure(request.Id, JsonRpcError.InvalidParams(ex.Message));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error handling server request '{Method}'", request.Method);
                    response = JsonRpcResponse.Failure(request.Id, JsonRpcError.InternalError(ex.Message));
                }
                if (!ct.IsCancellationRequested) await _transport.SendAsync(response, ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogWarning(ex, "Error sending handler response"); }
        })) _logger.LogWarning("Ignoring duplicate or closed inbound request {Id}", request.Id);
    }

    private async Task<JsonRpcResponse> DispatchServerRequestAsync(JsonRpcRequest request, CancellationToken ct)
    {
        if (_session.State != McpSessionState.Ready && request.Method != McpMethods.Ping)
            return JsonRpcResponse.Failure(request.Id, JsonRpcError.InvalidRequest("The MCP session is not ready."));
        var taskCapabilities = _options.BuildCapabilities().Tasks;
        var supportsTask = _session.Revision?.AtLeast(ProtocolRevision.V2025_11_25) == true && TaskProtocol.Supports(taskCapabilities, request.Method);
        if (request.Method.StartsWith("tasks/", StringComparison.Ordinal) && !supportsTask)
            return JsonRpcResponse.Failure(request.Id, JsonRpcError.MethodNotFound("Task operation is not supported."));
        if (request.Method is McpMethods.SamplingCreateMessage or McpMethods.ElicitationCreate && !supportsTask)
            request = TaskProtocol.WithoutAugmentation(request);
        ProtocolShapeValidation.Request(request, _session.Revision ?? ProtocolRevision.Latest);
        var response = await DispatchServerRequestCoreAsync(request, ct);
        if (!response.IsError)
        {
            try { ProtocolShapeValidation.Result(request, response.Result, _session.Revision ?? ProtocolRevision.Latest); }
            catch (JsonException ex) { return JsonRpcResponse.Failure(request.Id, JsonRpcError.InternalError(ex.Message)); }
        }
        return TaskExecutionContext.RelateResponse(request, response);
    }

    private async Task<JsonRpcResponse> DispatchServerRequestCoreAsync(JsonRpcRequest request, CancellationToken ct)
    {
        if (_session.State != McpSessionState.Ready && request.Method != McpMethods.Ping)
            return JsonRpcResponse.Failure(request.Id, JsonRpcError.InvalidRequest("The MCP session is not ready."));
        switch (request.Method)
        {
            case McpMethods.Ping:
                return JsonRpcResponse.Success(request.Id);

            case McpMethods.RootsList:
                if (_options.RootProvider is null)
                    return JsonRpcResponse.Failure(request.Id, JsonRpcError.MethodNotFound("Roots not supported"));

                var roots = _options.RootProvider.GetRoots();
                return JsonRpcResponse.Success(request.Id,
                    ToWire(new ListRootsResult { Roots = roots }));

            case McpMethods.SamplingCreateMessage:
                if (_options.SamplingHandler is null)
                    return JsonRpcResponse.Failure(request.Id, JsonRpcError.MethodNotFound("Sampling not supported"));

                var samplingReq = request.GetParams<CreateMessageRequest>()!;
                PeerRequestValidation.Sampling(samplingReq, _options.BuildCapabilities(), _session.Revision ?? ProtocolRevision.Latest);
                if (TryGetTaskMetadata(request.Params, out var samplingTaskMeta))
                {
                    var task = _taskStore.Create(samplingTaskMeta, _taskOwnerKey);
                    RunHandlerAsTask(task.TaskId, request, ct => _options.SamplingHandler.HandleAsync(samplingReq, ct));
                    return JsonRpcResponse.Success(request.Id, ToWire(new CreateTaskResult { Task = task }));
                }
                var samplingResult = await _options.SamplingHandler.HandleAsync(samplingReq, ct);
                return JsonRpcResponse.Success(request.Id, ToWire(samplingResult));


            case McpMethods.ElicitationCreate:
                if (_options.ElicitationHandler is null)
                    return JsonRpcResponse.Failure(request.Id, JsonRpcError.MethodNotFound("Elicitation not supported"));

                var elicitReq = request.GetParams<ElicitRequest>()!;
                PeerRequestValidation.Elicitation(elicitReq, _options.BuildCapabilities(), _session.Revision ?? ProtocolRevision.Latest);
                if (TryGetTaskMetadata(request.Params, out var elicitTaskMeta))
                {
                    var task = _taskStore.Create(elicitTaskMeta, _taskOwnerKey);
                    RunHandlerAsTask(task.TaskId, request, ct => _options.ElicitationHandler.HandleAsync(elicitReq, ct));
                    return JsonRpcResponse.Success(request.Id, ToWire(new CreateTaskResult { Task = task }));
                }
                var elicitResult = await _options.ElicitationHandler.HandleAsync(elicitReq, ct);
                return JsonRpcResponse.Success(request.Id, ToWire(elicitResult));


            case McpMethods.TasksGet:
                return HandleClientTaskGet(request);
            case McpMethods.TasksList:
                var page = _taskPagination.GetPage(_taskStore.List(_taskOwnerKey), request.GetParams<PaginatedRequest>()?.Cursor);
                return JsonRpcResponse.Success(request.Id, ToWire(new ListTasksResult { Tasks = page.Items, NextCursor = page.NextCursor }));
            case McpMethods.TasksResult:
                return await TaskResults.WaitAsync(_taskStore, _taskOwnerKey, request, ct);
            case McpMethods.TasksCancel:
                return HandleClientTaskCancel(request);

            default:
                if (_customHandlers.TryGetValue(request.Method, out var handler))
                    return JsonRpcResponse.Success(request.Id, ExtensionMethods.ObjectResult(await handler(request.Params, ct)));
                return JsonRpcResponse.Failure(request.Id,
                    JsonRpcError.MethodNotFound($"Client does not handle '{request.Method}'"));
        }
    }

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

    private void RunHandlerAsTask<T>(string taskId, JsonRpcRequest request, Func<CancellationToken, Task<T>> handler)
    {
        _background.Run(taskId, _cts?.Token ?? CancellationToken.None, async ct =>
        {
            using var context = new TaskExecutionContext(_taskStore, taskId);
            try
            {
                var result = await handler(ct);
                var payload = ToWire(result);
                var parameters = System.Text.Json.Nodes.JsonNode.Parse(request.Params!.Value.GetRawText())!.AsObject();
                parameters.Remove("task");
                try
                {
                    ProtocolShapeValidation.Result(request with { Params = JsonSerializer.SerializeToElement(parameters) }, payload,
                        _session.Revision ?? ProtocolRevision.Latest);
                    _taskStore.SetResult(taskId, payload);
                }
                catch (JsonException ex) { _taskStore.SetError(taskId, JsonRpcError.InternalError(ex.Message)); }
            }
            catch (Exception ex)
            {
                _taskStore.SetError(taskId, ex is ArgumentException or JsonException or McpCapabilityNotAvailableException
                    ? JsonRpcError.InvalidParams(ex.Message) : JsonRpcError.InternalError(ex.Message));
            }
        });
    }

    private JsonRpcResponse HandleClientTaskGet(JsonRpcRequest request)
    {
        var taskId = request.GetParams<TaskIdParams>()!.TaskId;
        var task = _taskStore.Get(taskId, _taskOwnerKey);
        return task is null
            ? JsonRpcResponse.Failure(request.Id, JsonRpcError.InvalidParams($"Unknown task: '{taskId}'"))
            : JsonRpcResponse.Success(request.Id, ToWire(task));
    }

    private JsonRpcResponse HandleClientTaskCancel(JsonRpcRequest request)
    {
        var taskId = request.GetParams<TaskIdParams>()!.TaskId;
        var task = _taskStore.Cancel(taskId, _taskOwnerKey);
        if (task is not null) _background.Cancel(taskId);
        return task is null
            ? JsonRpcResponse.Failure(request.Id, JsonRpcError.InvalidParams($"Unknown task: '{taskId}'"))
            : JsonRpcResponse.Success(request.Id, ToWire(task));
    }

    #endregion

    private void OnSessionReinitialized(InitializeResult result)
    {
        // Swap before cancellation: a retiring handler can itself have triggered recovery.
        var inbound = Interlocked.Exchange(ref _inbound, new InboundRequestRegistry());
        var background = Interlocked.Exchange(ref _background, new InboundRequestRegistry());
        _session.RefreshInitialization(result);
        _taskOwnerKey = _options.TaskOwnerKey ?? Guid.NewGuid().ToString("N");
        lock (_retiredHandlers)
        {
            _retiredHandlers.Add(inbound.StopAsync());
            _retiredHandlers.Add(background.StopAsync());
            _retiredHandlers.RemoveAll(task => task.IsCompletedSuccessfully);
        }
    }

    private void OnTransportDisconnected(object? sender, TransportDisconnectedEventArgs e)
    {
        _tracker.CancelAll("Transport disconnected");
        _cts?.Cancel();
        Disconnected?.Invoke(this, e);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_rootsChangedHandler is not null && _options.RootProvider is not null)
            _options.RootProvider.RootsChanged -= _rootsChangedHandler;

        if (_transport is StreamableHttpClientTransport http) http.SessionReinitialized -= OnSessionReinitialized;
        _tracker.CancelAll("Client disposing");
        _cts?.Cancel();

        try { if (_messageLoop is not null) await _messageLoop; } catch { }

        await _inbound.StopAsync();
        await _background.StopAsync();
        Task[] retired;
        lock (_retiredHandlers) retired = _retiredHandlers.ToArray();
        await Task.WhenAll(retired);
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
