using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Andy.MCP.Configuration;

/// <summary>
/// DI registration extensions for Andy.MCP.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add MCP client services with fluent configuration.
    /// </summary>
    public static IServiceCollection AddMcpClient(this IServiceCollection services, Action<McpClientOptions> configure)
    {
        var options = new McpClientOptions();
        configure(options);
        return services.AddMcpClientCore(options);
    }

    /// <summary>
    /// Add MCP client services from configuration section.
    /// </summary>
    public static IServiceCollection AddMcpClient(this IServiceCollection services, IConfigurationSection section)
    {
        var options = new McpClientOptions();
        section.Bind(options);
        return services.AddMcpClientCore(options);
    }

    private static IServiceCollection AddMcpClientCore(this IServiceCollection services, McpClientOptions options)
    {
        services.TryAddSingleton(options);
        services.TryAddSingleton<IMcpConnectionManager, McpConnectionManager>();
        services.AddHostedService<McpHostedService>();
        return services;
    }
}

/// <summary>
/// Fluent extensions for McpClientOptions to add servers.
/// </summary>
public static class McpClientOptionsExtensions
{
    public static McpClientOptions AddStdioServer(this McpClientOptions options, string name, string command,
        string? args = null, string? workingDirectory = null, Dictionary<string, string>? environment = null)
    {
        options.Servers.Add(new McpServerConfig
        {
            Name = name,
            Transport = "stdio",
            Command = command,
            Arguments = args,
            WorkingDirectory = workingDirectory,
            Environment = environment
        });
        return options;
    }

    public static McpClientOptions AddHttpServer(this McpClientOptions options, string name, string url)
    {
        options.Servers.Add(new McpServerConfig
        {
            Name = name,
            Transport = "http",
            Url = url
        });
        return options;
    }

    public static McpClientOptions AddGatewayServer(this McpClientOptions options, string name,
        string gatewayUrl, string adapterName)
    {
        options.Servers.Add(new McpServerConfig
        {
            Name = name,
            Transport = "gateway",
            GatewayUrl = gatewayUrl,
            AdapterName = adapterName
        });
        return options;
    }
}
