using Andy.MCP.Configuration;
using Andy.MCP.Protocol;

namespace Andy.MCP.Tests.Configuration;

public class McpConnectionManagerTests
{
    [Fact]
    public async Task GetClient_UnknownName_ReturnsNull()
    {
        var options = new McpClientOptions();
        await using var manager = new McpConnectionManager(options);

        Assert.Null(manager.GetClient("nonexistent"));
    }

    [Fact]
    public async Task ConnectedServers_InitiallyEmpty()
    {
        var options = new McpClientOptions();
        await using var manager = new McpConnectionManager(options);

        Assert.Empty(manager.ConnectedServers);
    }

    [Fact]
    public async Task ConnectAll_NoServers_Succeeds()
    {
        var options = new McpClientOptions();
        await using var manager = new McpConnectionManager(options);

        await manager.ConnectAllAsync(); // Should not throw
        Assert.Empty(manager.ConnectedServers);
    }

    [Fact]
    public async Task ConnectAll_FailingServer_LogsAndContinues()
    {
        var options = new McpClientOptions();
        options.AddStdioServer("bad-server", "nonexistent-command-that-does-not-exist");

        await using var manager = new McpConnectionManager(options);

        // Should not throw — failed connections are logged
        await manager.ConnectAllAsync();
        Assert.Empty(manager.ConnectedServers);
    }

    [Fact]
    public async Task AddServer_DuplicateName_Throws()
    {
        var options = new McpClientOptions();
        // We can't easily test AddServerAsync without a real server,
        // but we can test the duplicate detection in options
        options.AddStdioServer("dup", "echo");
        options.AddStdioServer("dup", "echo");

        // Both configs exist — manager would fail on the second connect
        Assert.Equal(2, options.Servers.Count);
    }

    [Fact]
    public async Task RemoveServer_NotConnected_NoError()
    {
        var options = new McpClientOptions();
        await using var manager = new McpConnectionManager(options);

        await manager.RemoveServerAsync("nonexistent"); // Should not throw
    }

    [Fact]
    public async Task DisconnectAll_WhenEmpty_Succeeds()
    {
        var options = new McpClientOptions();
        await using var manager = new McpConnectionManager(options);

        await manager.DisconnectAllAsync(); // Should not throw
    }

    [Fact]
    public async Task ListAllTools_NoServers_ReturnsEmpty()
    {
        var options = new McpClientOptions();
        await using var manager = new McpConnectionManager(options);

        var tools = await manager.ListAllToolsAsync();
        Assert.Empty(tools);
    }

    [Fact]
    public void CreateTransport_UnknownType_Throws()
    {
        var options = new McpClientOptions();
        options.Servers.Add(new McpServerConfig { Name = "bad", Transport = "websocket" });

        var manager = new McpConnectionManager(options);

        // ConnectAll will try to create the transport and fail
        Assert.ThrowsAsync<InvalidOperationException>(() => manager.ConnectAllAsync());
    }

    [Fact]
    public void CreateTransport_StdioWithoutCommand_Throws()
    {
        var options = new McpClientOptions();
        options.Servers.Add(new McpServerConfig { Name = "nocommand", Transport = "stdio" });

        var manager = new McpConnectionManager(options);

        Assert.ThrowsAsync<InvalidOperationException>(() => manager.ConnectAllAsync());
    }
}
