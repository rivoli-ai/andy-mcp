using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Transport;

namespace Andy.MCP.Containers;

public sealed record ContainerMcpResources(double CpuCores = 2, int MemoryMb = 2048, int DiskGb = 10);
public sealed record ContainerMcpProvisionOptions
{
    public required string Name { get; init; }
    public string? ProviderCode { get; init; }
    public Guid? WorkspaceId { get; init; }
    public string? OwnerId { get; init; }
    public ContainerMcpResources? Resources { get; init; }
    public Dictionary<string, string>? EnvironmentVariables { get; init; }
    public TimeSpan? ExpiresAfter { get; init; }
}
public sealed record ContainerMcpServer(Guid Id, string Name, Uri Endpoint, string Status, bool IsHealthy);
public sealed record ContainerMcpConnectionInfo(string? IpAddress, Dictionary<int, int>? PortMappings);
public sealed record ContainerMcpFilter(Guid? WorkspaceId = null, string? OwnerId = null, Guid? TemplateId = null);

/// <summary>Container control-plane and MCP endpoint settings. Credentials belong on the supplied control-plane HttpClient.</summary>
public sealed class ContainerMcpOptions
{
    public required Uri ApiUri { get; set; }
    public int McpPort { get; set; } = 3000;
    public string McpPath { get; set; } = "/mcp";
    public TimeSpan ProvisionTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>Resolve remote-provider mappings; default uses the API host and published MCP port.</summary>
    public Func<ContainerMcpConnectionInfo, Uri>? ResolveEndpoint { get; set; }
    /// <summary>Optional registration hook, for example registering a successfully provisioned endpoint in a gateway.</summary>
    public Func<ContainerMcpServer, CancellationToken, Task>? RegisterAsync { get; set; }
    public Func<Guid, CancellationToken, Task>? UnregisterAsync { get; set; }
}

public interface IContainerMcpServerProvider
{
    Task<ContainerMcpServer> ProvisionAsync(string templateCode, ContainerMcpProvisionOptions options, CancellationToken cancellationToken = default);
    Task DestroyAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ContainerMcpServer>> ListRunningAsync(ContainerMcpFilter? filter = null, CancellationToken cancellationToken = default);
    Task<bool> GetHealthAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ContainerMcpSession> OpenSessionAsync(Guid id, CancellationToken cancellationToken = default);
    Task CleanupIdleAsync(CancellationToken cancellationToken = default);
}

/// <summary>An active MCP client lease. Disposing releases the idle-cleanup guard.</summary>
public sealed class ContainerMcpSession : IAsyncDisposable
{
    private readonly Action _release;
    private int _disposed;
    private int _released;
    public McpClient Client { get; }
    internal ContainerMcpSession(McpClient client, Action release)
    {
        Client = client;
        _release = release;
        Client.Disconnected += OnDisconnected;
    }
    private void OnDisconnected(object? sender, TransportDisconnectedEventArgs args) => Release();
    private void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) _release();
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await Client.DisposeAsync().ConfigureAwait(false); }
        finally { Client.Disconnected -= OnDisconnected; Release(); }
    }
}

/// <summary>Provider-neutral adapter over Andy Containers REST provisioning and Streamable HTTP MCP.</summary>
public sealed class ContainerMcpServerProvider : IContainerMcpServerProvider
{
    private sealed record Container(Guid Id, string Name, string Status);
    private sealed class MissingMcpPortException() : InvalidOperationException("Container has no published MCP port; declare it in the template.");
    private sealed record Page(List<Container> Items, int TotalCount);
    private sealed class Owned(ContainerMcpServer server)
    {
        public ContainerMcpServer Server { get; } = server;
        public int Sessions;
        public HashSet<ContainerMcpSession> Leases { get; } = [];
        public long LastUsedTicks = DateTimeOffset.UtcNow.UtcTicks;
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly ContainerMcpOptions _options;
    private readonly Uri _api;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Dictionary<Guid, Owned> _owned = [];

    public ContainerMcpServerProvider(HttpClient http, ContainerMcpOptions options)
    {
        _http = http;
        _options = options;
        ValidateEndpoint(options.ApiUri);
        if (options.McpPort is < 1 or > 65535 || options.ProvisionTimeout <= TimeSpan.Zero
            || options.RequestTimeout <= TimeSpan.Zero || options.PollInterval <= TimeSpan.Zero || options.IdleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (!options.McpPath.StartsWith('/') || options.McpPath.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("MCP path must be an absolute path on the resolved host.", nameof(options));
        _api = new Uri(options.ApiUri.AbsoluteUri.TrimEnd('/') + "/api/containers/");
    }

    public async Task<ContainerMcpServer> ProvisionAsync(string templateCode, ContainerMcpProvisionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Name);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ProvisionTimeout);
        var ct = timeout.Token;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        Guid? created = null;
        try
        {
            var container = await SendAsync<Container>(HttpMethod.Post, "", new
            {
                options.Name,
                TemplateCode = templateCode,
                options.ProviderCode,
                options.WorkspaceId,
                options.OwnerId,
                options.Resources,
                options.EnvironmentVariables,
                options.ExpiresAfter,
                Source = "Mcp"
            }, ct).ConfigureAwait(false);
            if (container.Id == Guid.Empty) throw new JsonException("Container creation returned an empty id.");
            created = container.Id;
            var started = false;
            while (!container.Status.Equals("Running", StringComparison.OrdinalIgnoreCase))
            {
                if (container.Status is "Failed" or "Destroyed" or "Destroying") throw new InvalidOperationException($"Container {container.Id} entered {container.Status}.");
                if (container.Status.Equals("Stopped", StringComparison.OrdinalIgnoreCase) && !started)
                {
                    await SendAsync<object>(HttpMethod.Post, $"{container.Id}/start", null, ct).ConfigureAwait(false);
                    started = true;
                }
                await Task.Delay(_options.PollInterval, ct).ConfigureAwait(false);
                container = await SendAsync<Container>(HttpMethod.Get, container.Id.ToString(), null, ct).ConfigureAwait(false);
            }
            var endpoint = await EndpointAsync(container.Id, ct).ConfigureAwait(false);
            // Running infrastructure is not enough: wait for the MCP application to be ready.
            while (!await ProbeAsync(endpoint, ct).ConfigureAwait(false)) await Task.Delay(_options.PollInterval, ct).ConfigureAwait(false);
            var server = new ContainerMcpServer(container.Id, container.Name, endpoint, container.Status, true);
            if (_options.RegisterAsync is { } register) await register(server, ct).ConfigureAwait(false);
            _owned.Add(server.Id, new Owned(server));
            return server;
        }
        catch (Exception failure) when (created is { } id)
        {
            using var cleanup = new CancellationTokenSource(_options.RequestTimeout);
            try { await DestroyCoreAsync(id, cleanup.Token).ConfigureAwait(false); }
            catch (Exception cleanupFailure) { throw new AggregateException($"Provisioning and cleanup failed for container {id}.", failure, cleanupFailure); }
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task<ContainerMcpSession> OpenSessionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_owned.TryGetValue(id, out var owned)) throw new InvalidOperationException("Only containers provisioned by this provider can acquire tracked sessions.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.RequestTimeout);
            var state = await SendAsync<Container>(HttpMethod.Get, id.ToString(), null, timeout.Token).ConfigureAwait(false);
            if (!state.Status.Equals("Running", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Container is not running.");
            var endpoint = await EndpointAsync(id, timeout.Token).ConfigureAwait(false);
            var client = await ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
            lock (owned)
            {
                Interlocked.Increment(ref owned.Sessions);
                ContainerMcpSession? lease = null;
                lease = new ContainerMcpSession(client, () =>
                {
                    lock (owned)
                    {
                        if (lease is not null) owned.Leases.Remove(lease);
                        Interlocked.Exchange(ref owned.LastUsedTicks, DateTimeOffset.UtcNow.UtcTicks);
                        Interlocked.Decrement(ref owned.Sessions);
                    }
                });
                owned.Leases.Add(lease);
                return lease;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task DestroyAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_owned.TryGetValue(id, out var owned) && Volatile.Read(ref owned.Sessions) > 0)
                throw new InvalidOperationException("Container has active MCP sessions.");
            await DestroyCoreAsync(id, cancellationToken).ConfigureAwait(false);
            _owned.Remove(id);
        }
        finally { _gate.Release(); }
    }

    private async Task DestroyCoreAsync(Guid id, CancellationToken ct)
    {
        var errors = new List<Exception>();
        if (_options.UnregisterAsync is { } unregister)
        {
            try { await unregister(id, ct).ConfigureAwait(false); }
            catch (Exception ex) { errors.Add(ex); }
        }
        try { await SendAsync<object>(HttpMethod.Post, $"{id}/stop", null, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.BadRequest) { }
        catch (Exception ex) { errors.Add(ex); }
        // A failing gateway callback or stop request must not prevent a destroy attempt.
        try { await SendAsync<object>(HttpMethod.Delete, id.ToString(), null, ct).ConfigureAwait(false); }
        catch (Exception ex) { errors.Add(ex); }
        if (errors.Count > 0) throw new AggregateException($"Cleanup encountered errors for container {id}.", errors);
    }

    public async Task CleanupIdleAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var (id, owned) in _owned.ToArray())
            {
                if (Volatile.Read(ref owned.Sessions) > 0 && !await GetHealthAsync(id, cancellationToken).ConfigureAwait(false))
                {
                    ContainerMcpSession[] leases;
                    lock (owned) leases = owned.Leases.ToArray();
                    foreach (var lease in leases) await lease.DisposeAsync().ConfigureAwait(false);
                }
                if (Volatile.Read(ref owned.Sessions) != 0 || DateTimeOffset.UtcNow.UtcTicks - Interlocked.Read(ref owned.LastUsedTicks) < _options.IdleTimeout.Ticks) continue;
                await DestroyCoreAsync(id, cancellationToken).ConfigureAwait(false);
                _owned.Remove(id);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ContainerMcpServer>> ListRunningAsync(ContainerMcpFilter? filter = null, CancellationToken cancellationToken = default)
    {
        var query = "?status=Running&take=100";
        if (filter?.OwnerId is { } owner) query += "&ownerId=" + Uri.EscapeDataString(owner);
        if (filter?.WorkspaceId is { } workspace) query += "&workspaceId=" + workspace;
        if (filter?.TemplateId is { } template) query += "&templateId=" + template;
        var servers = new List<ContainerMcpServer>();
        for (var skip = 0; ;)
        {
            var page = await SendAsync<Page>(HttpMethod.Get, query + "&skip=" + skip, null, cancellationToken).ConfigureAwait(false);
            foreach (var container in page.Items)
            {
                if (!container.Status.Equals("Running", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var endpoint = await EndpointAsync(container.Id, cancellationToken).ConfigureAwait(false);
                    servers.Add(new(container.Id, container.Name, endpoint, container.Status, await ProbeAsync(endpoint, cancellationToken).ConfigureAwait(false)));
                }
                catch (MissingMcpPortException) { /* A running non-MCP container is outside this catalog. */ }
            }
            skip += page.Items.Count;
            if (page.Items.Count == 0 || skip >= page.TotalCount) return servers;
        }
    }

    public async Task<bool> GetHealthAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            var container = await SendAsync<Container>(HttpMethod.Get, id.ToString(), null, cancellationToken).ConfigureAwait(false);
            return container.Status.Equals("Running", StringComparison.OrdinalIgnoreCase)
                && await ProbeAsync(await EndpointAsync(id, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return false; }
    }

    private async Task<Uri> EndpointAsync(Guid id, CancellationToken ct)
    {
        var info = await SendAsync<ContainerMcpConnectionInfo>(HttpMethod.Get, $"{id}/connection", null, ct).ConfigureAwait(false);
        Uri endpoint;
        if (_options.ResolveEndpoint is { } resolver) endpoint = resolver(info);
        else
        {
            if (info.PortMappings is null || !info.PortMappings.TryGetValue(_options.McpPort, out var port)) throw new MissingMcpPortException();
            endpoint = new UriBuilder(Uri.UriSchemeHttp, _options.ApiUri.Host, port, _options.McpPath).Uri;
        }
        ValidateEndpoint(endpoint);
        return endpoint;
    }

    private static void ValidateEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException("An absolute HTTP(S) endpoint without credentials or fragment is required.", nameof(endpoint));
    }

    /// <summary>Creates a caller-owned MCP transport for a resolved container endpoint.</summary>
    public static IClientTransport CreateTransport(Uri endpoint)
    {
        ValidateEndpoint(endpoint);
        return new StreamableHttpClientTransport(new() { Endpoint = endpoint, EnableServerSseStream = false });
    }

    private static Task<McpClient> ConnectAsync(Uri endpoint, CancellationToken ct) => McpClient.ConnectAsync(
        CreateTransport(endpoint), cancellationToken: ct);

    private async Task<bool> ProbeAsync(Uri endpoint, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.RequestTimeout);
        try
        {
            await using var client = await ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
            await client.PingAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or McpException) { return false; }
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.RequestTimeout);
        using var request = new HttpRequestMessage(method, new Uri(_api, path));
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (method == HttpMethod.Delete && response.StatusCode == HttpStatusCode.NotFound) return default!;
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.NoContent || method == HttpMethod.Delete || path.EndsWith("/stop", StringComparison.Ordinal) || path.EndsWith("/start", StringComparison.Ordinal)) return default!;
        return await response.Content.ReadFromJsonAsync<T>(Json, timeout.Token).ConfigureAwait(false) ?? throw new JsonException("Container API returned an empty response.");
    }
}
