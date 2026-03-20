using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Andy.MCP.Configuration;

/// <summary>
/// Background service that manages MCP server connections lifecycle.
/// Connects on startup, disconnects on shutdown.
/// </summary>
public sealed class McpHostedService : IHostedService
{
    private readonly IMcpConnectionManager _connectionManager;
    private readonly ILogger<McpHostedService> _logger;

    public McpHostedService(IMcpConnectionManager connectionManager, ILogger<McpHostedService> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MCP hosted service starting — connecting to configured servers");
        await _connectionManager.ConnectAllAsync(cancellationToken);
        _logger.LogInformation("MCP hosted service started — {Count} servers connected",
            _connectionManager.ConnectedServers.Count);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MCP hosted service stopping — disconnecting all servers");
        await _connectionManager.DisconnectAllAsync();
        _logger.LogInformation("MCP hosted service stopped");
    }
}
