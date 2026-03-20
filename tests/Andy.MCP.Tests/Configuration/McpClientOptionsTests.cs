using Andy.MCP.Configuration;
using Microsoft.Extensions.Configuration;

namespace Andy.MCP.Tests.Configuration;

public class McpClientOptionsTests
{
    [Fact]
    public void FluentApi_AddStdioServer()
    {
        var options = new McpClientOptions();
        options.AddStdioServer("fs", "/usr/bin/mcp-fs", args: "--root /data",
            workingDirectory: "/tmp",
            environment: new() { ["DEBUG"] = "true" });

        Assert.Single(options.Servers);
        var server = options.Servers[0];
        Assert.Equal("fs", server.Name);
        Assert.Equal("stdio", server.Transport);
        Assert.Equal("/usr/bin/mcp-fs", server.Command);
        Assert.Equal("--root /data", server.Arguments);
        Assert.Equal("/tmp", server.WorkingDirectory);
        Assert.Equal("true", server.Environment!["DEBUG"]);
    }

    [Fact]
    public void FluentApi_AddHttpServer()
    {
        var options = new McpClientOptions();
        options.AddHttpServer("remote", "https://tools.example.com/mcp");

        Assert.Single(options.Servers);
        Assert.Equal("remote", options.Servers[0].Name);
        Assert.Equal("http", options.Servers[0].Transport);
        Assert.Equal("https://tools.example.com/mcp", options.Servers[0].Url);
    }

    [Fact]
    public void FluentApi_AddGatewayServer()
    {
        var options = new McpClientOptions();
        options.AddGatewayServer("gw", "https://gateway.rivoli.ai", "my-adapter");

        Assert.Single(options.Servers);
        Assert.Equal("gw", options.Servers[0].Name);
        Assert.Equal("gateway", options.Servers[0].Transport);
        Assert.Equal("https://gateway.rivoli.ai", options.Servers[0].GatewayUrl);
        Assert.Equal("my-adapter", options.Servers[0].AdapterName);
    }

    [Fact]
    public void FluentApi_MultipleServers()
    {
        var options = new McpClientOptions();
        options
            .AddStdioServer("fs", "mcp-fs")
            .AddHttpServer("remote", "https://example.com/mcp")
            .AddGatewayServer("gw", "https://gw.example.com", "adapter1");

        Assert.Equal(3, options.Servers.Count);
    }

    [Fact]
    public void ConfigBinding_FromJson()
    {
        var json = """
        {
            "McpClient": {
                "ClientInfo": { "Name": "TestApp", "Version": "2.0.0" },
                "RequestTimeout": "00:01:00",
                "AutoReconnect": true,
                "ReconnectPolicy": {
                    "MaxRetries": 10,
                    "BaseDelay": "00:00:02",
                    "Strategy": "Exponential",
                    "MaxDelay": "00:05:00"
                },
                "Servers": [
                    {
                        "Name": "local-fs",
                        "Transport": "stdio",
                        "Command": "/usr/bin/mcp-fs",
                        "Arguments": "--root /data"
                    },
                    {
                        "Name": "remote-tools",
                        "Transport": "http",
                        "Url": "https://tools.example.com/mcp"
                    }
                ]
            }
        }
        """;

        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();

        var options = new McpClientOptions();
        config.GetSection(McpClientOptions.SectionName).Bind(options);

        Assert.Equal("TestApp", options.ClientInfo.Name);
        Assert.Equal("2.0.0", options.ClientInfo.Version);
        Assert.Equal(TimeSpan.FromMinutes(1), options.RequestTimeout);
        Assert.True(options.AutoReconnect);
        Assert.Equal(10, options.ReconnectPolicy.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(2), options.ReconnectPolicy.BaseDelay);
        Assert.Equal("Exponential", options.ReconnectPolicy.Strategy);
        Assert.Equal(TimeSpan.FromMinutes(5), options.ReconnectPolicy.MaxDelay);

        Assert.Equal(2, options.Servers.Count);
        Assert.Equal("local-fs", options.Servers[0].Name);
        Assert.Equal("stdio", options.Servers[0].Transport);
        Assert.Equal("/usr/bin/mcp-fs", options.Servers[0].Command);
        Assert.Equal("remote-tools", options.Servers[1].Name);
        Assert.Equal("http", options.Servers[1].Transport);
    }

    [Fact]
    public void ToImplementation_Converts()
    {
        var options = new McpClientOptions
        {
            ClientInfo = new ImplementationConfig { Name = "MyApp", Version = "3.0.0" }
        };

        var impl = options.ToImplementation();
        Assert.Equal("MyApp", impl.Name);
        Assert.Equal("3.0.0", impl.Version);
    }

    [Fact]
    public void Defaults_AreReasonable()
    {
        var options = new McpClientOptions();

        Assert.Equal("Andy.MCP", options.ClientInfo.Name);
        Assert.Equal(TimeSpan.FromSeconds(30), options.RequestTimeout);
        Assert.False(options.AutoReconnect);
        Assert.Equal(5, options.ReconnectPolicy.MaxRetries);
        Assert.Empty(options.Servers);
    }
}
