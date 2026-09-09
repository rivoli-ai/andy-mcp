using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Andy.MCP.Containers;

/// <summary>Runs idle cleanup for containers owned by this provider instance.</summary>
public sealed class ContainerMcpCleanupService(IContainerMcpServerProvider provider, ContainerMcpOptions options,
    ILogger<ContainerMcpCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(options.PollInterval, stoppingToken).ConfigureAwait(false);
                try { await provider.CleanupIdleAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogWarning(ex, "MCP container idle cleanup failed; retrying on next pass."); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

public static class ContainerMcpServiceCollectionExtensions
{
    /// <summary>Registers a container provider using a caller-owned authenticated control-plane HttpClient.</summary>
    public static IServiceCollection AddContainerMcpServers(this IServiceCollection services, ContainerMcpOptions options,
        Func<IServiceProvider, HttpClient> httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        services.TryAddSingleton(options);
        services.TryAddSingleton<IContainerMcpServerProvider>(sp => new ContainerMcpServerProvider(httpClientFactory(sp), options));
        services.AddHostedService<ContainerMcpCleanupService>();
        return services;
    }
}
