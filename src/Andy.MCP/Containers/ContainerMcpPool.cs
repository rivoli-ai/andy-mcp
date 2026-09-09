using Andy.MCP.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Andy.MCP.Containers;

public sealed class ContainerMcpPoolOptions
{
    public required string TemplateCode { get; init; }
    public required ContainerMcpProvisionOptions ProvisionOptions { get; init; }
    public int MinimumSize { get; init; } = 1;
    public int MaximumSize { get; init; } = 4;
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>A single-caller lease on a pooled container. Dispose returns capacity to the pool.</summary>
public sealed class ContainerMcpPoolLease : IAsyncDisposable
{
    private readonly Func<Task> _release;
    private int _released;
    public ContainerMcpServer Server { get; }
    public McpClient Client { get; }
    internal ContainerMcpPoolLease(ContainerMcpServer server, McpClient client, Func<Task> release)
    {
        Server = server;
        Client = client;
        _release = release;
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) await _release().ConfigureAwait(false);
    }
}

/// <summary>
/// An instance-local bounded pool. Warm capacity is protected from provider idle cleanup;
/// demand grows the pool to MaximumSize, and maintenance removes excess idle capacity.
/// Returning a lease opens a fresh MCP session. Container filesystem/application state persists.
/// </summary>
public sealed class ContainerMcpPool : IAsyncDisposable
{
    private sealed class Slot(ContainerMcpServer server, ContainerMcpSession session)
    {
        public ContainerMcpServer Server { get; } = server;
        public ContainerMcpSession Session = session;
        public bool Busy;
        public DateTimeOffset LastUsed = DateTimeOffset.UtcNow;
    }
    private readonly IContainerMcpServerProvider _provider;
    private readonly ContainerMcpPoolOptions _options;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly List<Slot> _slots = [];
    private bool _disposed;

    public ContainerMcpPool(IContainerMcpServerProvider provider, ContainerMcpPoolOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TemplateCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProvisionOptions.Name);
        if (options.MinimumSize < 0 || options.MaximumSize < 1 || options.MinimumSize > options.MaximumSize
            || options.IdleTimeout <= TimeSpan.Zero || options.PollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        _provider = provider;
        _options = options;
    }

    public async Task WarmAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await WarmCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task WarmCoreAsync(CancellationToken ct)
    {
        while (_slots.Count < _options.MinimumSize) await CreateAsync(ct).ConfigureAwait(false);
    }

    private async Task<Slot> CreateAsync(CancellationToken ct)
    {
        var options = _options.ProvisionOptions with { Name = $"{_options.ProvisionOptions.Name}-{Guid.NewGuid():N}" };
        var server = await _provider.ProvisionAsync(_options.TemplateCode, options, ct).ConfigureAwait(false);
        try
        {
            var slot = new Slot(server, await _provider.OpenSessionAsync(server.Id, ct).ConfigureAwait(false));
            _slots.Add(slot);
            return slot;
        }
        catch (Exception failure)
        {
            try { await _provider.DestroyAsync(server.Id, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException("Pool session creation and cleanup failed.", failure, cleanup); }
            throw;
        }
    }

    /// <summary>Acquires exclusive capacity; throws when all MaximumSize containers are leased.</summary>
    public async Task<ContainerMcpPoolLease> RentAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var idle in _slots.Where(s => !s.Busy).ToArray())
            {
                if (await _provider.GetHealthAsync(idle.Server.Id, cancellationToken).ConfigureAwait(false))
                {
                    try { await idle.Session.Client.PingAsync(cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch { await RemoveAsync(idle, cancellationToken).ConfigureAwait(false); continue; }
                    return Lease(idle);
                }
                await RemoveAsync(idle, cancellationToken).ConfigureAwait(false);
            }
            if (_slots.Count >= _options.MaximumSize) throw new InvalidOperationException("MCP container pool is at capacity.");
            return Lease(await CreateAsync(cancellationToken).ConfigureAwait(false));
        }
        finally { _gate.Release(); }
    }

    private ContainerMcpPoolLease Lease(Slot slot)
    {
        slot.Busy = true;
        return new(slot.Server, slot.Session.Client, () => ReturnAsync(slot));
    }

    private async Task ReturnAsync(Slot slot)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || !_slots.Contains(slot)) return;
            slot.Busy = false;
            await slot.Session.DisposeAsync().ConfigureAwait(false);
            try
            {
                slot.Session = await _provider.OpenSessionAsync(slot.Server.Id).ConfigureAwait(false);
                slot.LastUsed = DateTimeOffset.UtcNow;
                slot.Busy = false;
            }
            catch
            {
                await RemoveAsync(slot, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    private async Task RemoveAsync(Slot slot, CancellationToken ct)
    {
        await slot.Session.DisposeAsync().ConfigureAwait(false);
        await _provider.DestroyAsync(slot.Server.Id, ct).ConfigureAwait(false);
        _slots.Remove(slot);
    }

    /// <summary>Replaces unhealthy idle capacity, trims excess idle containers and restores the minimum.</summary>
    public async Task MaintainAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var slot in _slots.Where(s => !s.Busy).ToArray())
            {
                if (!await _provider.GetHealthAsync(slot.Server.Id, cancellationToken).ConfigureAwait(false)
                    || (_slots.Count > _options.MinimumSize && DateTimeOffset.UtcNow - slot.LastUsed >= _options.IdleTimeout))
                    await RemoveAsync(slot, cancellationToken).ConfigureAwait(false);
            }
            await WarmCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Closes all leases and destroys this pool's containers, including checked-out capacity.</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            var errors = new List<Exception>();
            foreach (var slot in _slots.ToArray())
            {
                try { await RemoveAsync(slot, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { errors.Add(ex); }
            }
            if (errors.Count > 0) throw new AggregateException("MCP pool cleanup failed.", errors);
        }
        finally { _gate.Release(); }
    }
}

internal sealed class ContainerMcpPoolService(ContainerMcpPool pool, ContainerMcpPoolOptions options,
    ILogger<ContainerMcpPoolService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await pool.MaintainAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogWarning(ex, "MCP pool maintenance failed; retrying."); }
                await Task.Delay(options.PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await pool.DisposeAsync().ConfigureAwait(false);
    }
}

public static class ContainerMcpPoolServiceCollectionExtensions
{
    /// <summary>Adds bounded pooling over the registered IContainerMcpServerProvider.</summary>
    public static IServiceCollection AddContainerMcpPool(this IServiceCollection services, ContainerMcpPoolOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<ContainerMcpPool>();
        services.AddHostedService<ContainerMcpPoolService>();
        return services;
    }
}
