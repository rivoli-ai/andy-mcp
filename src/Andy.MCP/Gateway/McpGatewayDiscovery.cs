using Andy.MCP.Client;
using Andy.MCP.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Andy.MCP.Gateway;

/// <summary>Reconciles active registry endpoints with the connection manager and checks MCP health.</summary>
public sealed class McpGatewayDiscovery : BackgroundService
{
    private readonly IMcpGatewayClient _registry;
    private readonly IMcpConnectionManager _manager;
    private readonly McpGatewayOptions _options;
    private readonly ILogger<McpGatewayDiscovery> _logger;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Dictionary<string, (string Endpoint, McpClient Client)> _owned = new(StringComparer.Ordinal);
    private IReadOnlyList<GatewayRegistration>? _cache;
    private DateTimeOffset _cachedAt;

    public McpGatewayDiscovery(IMcpGatewayClient registry, IMcpConnectionManager manager,
        McpGatewayOptions options, ILogger<McpGatewayDiscovery> logger)
    {
        _registry = registry;
        _manager = manager;
        _options = options;
        _logger = logger;
        if (options.RefreshInterval <= TimeSpan.Zero || options.RequestTimeout <= TimeSpan.Zero || options.MaximumCacheAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    /// <summary>Refreshes the registry and probes existing MCP connections. Transient registry outages use a bounded cache.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<GatewayRegistration> entries;
            try
            {
                if (_options.UseAdapterProxy)
                {
                    await _registry.CheckAdaptersHealthAsync(cancellationToken).ConfigureAwait(false);
                    var adapters = await _registry.ListAdaptersAsync(cancellationToken).ConfigureAwait(false);
                    entries = adapters.Adapters.Select(a => new GatewayRegistration
                    {
                        Id = a.Id.ToString(),
                        Name = a.Name,
                        Endpoint = McpGatewayTransport.GetProxyEndpoint(_options.RegistryUri, a.Name, a.Type).AbsoluteUri,
                        Status = a.Enabled && a.IsHealthy ? GatewayStatus.Active : GatewayStatus.Inactive,
                        Version = a.Revision.ToString(),
                        Metadata = new() { ["adapterType"] = ((int)a.Type).ToString() }
                    }).ToArray();
                }
                else entries = await _registry.ListAsync(cancellationToken).ConfigureAwait(false);
                _cache = entries;
                _cachedAt = DateTimeOffset.UtcNow;
            }
            catch (Exception ex) when ((ex is HttpRequestException httpError && (httpError.StatusCode is null || (int)httpError.StatusCode >= 500))
                || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                _logger.LogWarning(ex, "Gateway registry unavailable; using bounded discovery cache.");
                entries = _cache is not null && DateTimeOffset.UtcNow - _cachedAt <= _options.MaximumCacheAge ? _cache : [];
            }
            var active = entries.Where(e => e.Status == GatewayStatus.Active && !string.IsNullOrWhiteSpace(e.Id))
                .GroupBy(e => e.Id, StringComparer.Ordinal).Where(g => g.Count() == 1)
                .ToDictionary(g => "gateway:" + g.Key, g => g.Single(), StringComparer.Ordinal);
            foreach (var name in _owned.Keys.Where(n => !active.ContainsKey(n)).ToArray()) await RemoveAsync(name).ConfigureAwait(false);
            foreach (var (name, entry) in active)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.RequestTimeout);
                try
                {
                    McpGatewayClient.ValidateEndpoint(new Uri(entry.Endpoint));
                    if (_owned.TryGetValue(name, out var previous))
                    {
                        if (!ReferenceEquals(_manager.GetClient(name), previous.Client)) _owned.Remove(name);
                        else if (previous.Endpoint != entry.Endpoint + "|" + entry.Version) await RemoveAsync(name).ConfigureAwait(false);
                        else
                        {
                            await previous.Client.PingAsync(timeout.Token).ConfigureAwait(false);
                            continue;
                        }
                    }
                    // This service only disconnects clients that it created.
                    if (_manager.GetClient(name) is not null) continue;
                    await _manager.AddServerAsync(new McpServerConfig
                    {
                        Name = name,
                        Transport = _options.UseAdapterProxy ? "gateway" : "http",
                        Url = entry.Endpoint,
                        GatewayUrl = _options.RegistryUri.AbsoluteUri,
                        AdapterName = entry.Name,
                        GatewayOptions = _options,
                        GatewayAdapterType = entry.Metadata.TryGetValue("adapterType", out var type) ? (McpAdapterType)int.Parse(type) : McpAdapterType.StreamableHttp
                    }, timeout.Token).ConfigureAwait(false);
                    var client = _manager.GetClient(name) ?? throw new InvalidOperationException("Connection manager did not retain the new client.");
                    _owned.Add(name, (entry.Endpoint + "|" + entry.Version, client));
                    await client.PingAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Gateway endpoint {Name} is unhealthy; retrying on next refresh.", name);
                    await RemoveAsync(name).ConfigureAwait(false);
                }
            }
        }
        finally { _gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RefreshAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { _logger.LogWarning(ex, "Gateway discovery failed; retrying after the refresh interval."); }
                await Task.Delay(_options.RefreshInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { foreach (var name in _owned.Keys.ToArray()) await RemoveAsync(name).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { foreach (var name in _owned.Keys.ToArray()) await RemoveAsync(name).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task RemoveAsync(string name)
    {
        if (_owned.Remove(name, out var owned) && ReferenceEquals(_manager.GetClient(name), owned.Client))
            await _manager.RemoveServerAsync(name).ConfigureAwait(false);
    }
}

public static class McpGatewayServiceCollectionExtensions
{
    /// <summary>Adds a gateway client with an explicit base URI and options configuration.</summary>
    public static IServiceCollection AddMcpGateway(this IServiceCollection services, Uri gatewayUri,
        Action<McpGatewayOptions> configure, Func<IServiceProvider, HttpClient>? httpClientFactory = null)
    {
        var options = new McpGatewayOptions { RegistryUri = gatewayUri };
        configure(options);
        return services.AddMcpGateway(options, httpClientFactory);
    }

    /// <summary>Adds a registry client. Customize its HttpClient for registry-specific authentication.</summary>
    public static IServiceCollection AddMcpGateway(this IServiceCollection services, McpGatewayOptions options,
        Func<IServiceProvider, HttpClient>? httpClientFactory = null)
    {
        services.TryAddSingleton(options);
        services.TryAddSingleton<IMcpGatewayClient>(sp => new McpGatewayClient(
            httpClientFactory?.Invoke(sp) ?? new HttpClient(), sp.GetRequiredService<McpGatewayOptions>(), ownsHttpClient: httpClientFactory is null));
        return services;
    }

    /// <summary>Adds registry discovery to an existing AddMcpClient connection manager.</summary>
    public static IServiceCollection AddMcpGatewayDiscovery(this IServiceCollection services)
    {
        services.TryAddSingleton<McpGatewayDiscovery>();
        services.AddHostedService(sp => sp.GetRequiredService<McpGatewayDiscovery>());
        return services;
    }
}
