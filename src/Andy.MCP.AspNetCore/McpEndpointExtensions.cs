using Andy.MCP.Server;
using Andy.MCP.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Andy.MCP.AspNetCore;

/// <summary>
/// Extension methods for mapping MCP Streamable HTTP endpoints.
/// </summary>
public static class McpEndpointExtensions
{
    /// <summary>
    /// Map an MCP Streamable HTTP endpoint at the specified path.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="pattern">The URL pattern (e.g., "/mcp").</param>
    /// <param name="configureServer">Configure the MCP server for each session.</param>
    /// <param name="options">Server transport options.</param>
    public static IEndpointConventionBuilder MapMcp(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Action<McpServer> configureServer,
        StreamableHttpServerOptions? options = null)
    {
        var loggerFactory = endpoints.ServiceProvider.GetService(typeof(ILoggerFactory))
            as ILoggerFactory;

        var handler = new StreamableHttpHandler(
            sessionHandler: async transport =>
            {
                var server = new McpServer(transport);
                configureServer(server);
                await server.RunAsync();
            },
            options: options,
            logger: loggerFactory is not null ? LoggerFactoryExtensions.CreateLogger<StreamableHttpHandler>(loggerFactory) : null);

        return endpoints.Map(pattern, handler.HandleAsync);
    }
}
