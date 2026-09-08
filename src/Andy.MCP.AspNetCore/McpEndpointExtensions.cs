using Andy.MCP.Server;
using Andy.MCP.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Andy.MCP.AspNetCore;

/// <summary>
/// Extension methods for mapping MCP Streamable HTTP endpoints.
/// </summary>
public static class McpEndpointExtensions
{
    /// <summary>Map an MCP endpoint with default server options.</summary>
    public static IEndpointConventionBuilder MapMcp(
        this IEndpointRouteBuilder endpoints, string pattern, Action<McpServer> configureServer,
        StreamableHttpServerOptions? options = null) =>
        MapMcp(endpoints, pattern, configureServer, options, null);

    /// <summary>
    /// Map an MCP Streamable HTTP endpoint at the specified path.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="pattern">The URL pattern (e.g., "/mcp").</param>
    /// <param name="configureServer">Configure the MCP server for each session.</param>
    /// <param name="serverOptions">Server configuration, including an optional shared task store. Ownership is always session-bound.</param>
    /// <param name="options">Server transport options.</param>
    public static IEndpointConventionBuilder MapMcp(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Action<McpServer> configureServer,
        StreamableHttpServerOptions? options,
        McpServerOptions? serverOptions)
    {
        var loggerFactory = endpoints.ServiceProvider.GetService(typeof(ILoggerFactory))
            as ILoggerFactory;

        var handler = new StreamableHttpHandler(
            sessionHandler: async transport =>
            {
                await using var server = new McpServer(transport, (serverOptions ?? new McpServerOptions()) with
                {
                    // Even a shared task store cannot expose tasks across HTTP sessions.
                    TaskOwnerKey = ((StreamableHttpSession)transport).SessionId
                });
                configureServer(server);
                await server.RunAsync();
            },
            options: options,
            logger: loggerFactory is not null ? LoggerFactoryExtensions.CreateLogger<StreamableHttpHandler>(loggerFactory) : null);

        if (options?.Authorization is { } auth)
            endpoints.MapGet(auth.MetadataPath, () => Results.Json(auth.Metadata)).AllowAnonymous();
        var lifetime = endpoints.ServiceProvider.GetService(typeof(IHostApplicationLifetime)) as IHostApplicationLifetime;
        lifetime?.ApplicationStopping.Register(handler.Dispose);
        return endpoints.Map(pattern, handler.HandleAsync);
    }
}
