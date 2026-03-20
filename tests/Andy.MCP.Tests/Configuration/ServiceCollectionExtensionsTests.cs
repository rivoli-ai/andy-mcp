using Andy.MCP.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Andy.MCP.Tests.Configuration;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddMcpClient_FluentApi_RegistersServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpClient(options =>
        {
            options.ClientInfo = new ImplementationConfig { Name = "Test", Version = "1.0" };
        });

        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<McpClientOptions>();
        Assert.Equal("Test", options.ClientInfo.Name);

        var manager = provider.GetRequiredService<IMcpConnectionManager>();
        Assert.NotNull(manager);
    }

    [Fact]
    public void AddMcpClient_FromConfig_RegistersServices()
    {
        var json = """
        {
            "McpClient": {
                "ClientInfo": { "Name": "FromConfig", "Version": "2.0" },
                "RequestTimeout": "00:00:45"
            }
        }
        """;

        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpClient(config.GetSection(McpClientOptions.SectionName));

        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<McpClientOptions>();
        Assert.Equal("FromConfig", options.ClientInfo.Name);
        Assert.Equal(TimeSpan.FromSeconds(45), options.RequestTimeout);
    }

    [Fact]
    public void AddMcpClient_ResolvesConnectionManager()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpClient(options => { });

        var provider = services.BuildServiceProvider();

        var manager1 = provider.GetRequiredService<IMcpConnectionManager>();
        var manager2 = provider.GetRequiredService<IMcpConnectionManager>();

        Assert.Same(manager1, manager2); // Singleton
    }

    [Fact]
    public void AddMcpClient_WithServers_ConfiguresCorrectly()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpClient(options =>
        {
            options.AddStdioServer("test-server", "echo", args: "hello");
        });

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<McpClientOptions>();

        Assert.Single(options.Servers);
        Assert.Equal("test-server", options.Servers[0].Name);
    }
}
