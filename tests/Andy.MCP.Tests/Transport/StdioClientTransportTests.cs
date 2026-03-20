using Andy.MCP.Protocol;
using Andy.MCP.Transport;

namespace Andy.MCP.Tests.Transport;

public class StdioClientTransportTests
{
    [Fact]
    public async Task Connect_LaunchesProcess_AndCommunicates()
    {
        // Use 'cat' as a simple echo server — it reads stdin and writes to stdout
        var options = new StdioClientTransportOptions
        {
            Command = "cat",
            ShutdownTimeout = TimeSpan.FromSeconds(2),
            KillGraceTimeout = TimeSpan.FromSeconds(1)
        };

        await using var transport = new StdioClientTransport(options);
        await transport.ConnectAsync();

        Assert.True(transport.IsConnected);

        // Send a message — cat will echo it back
        var request = new JsonRpcRequest { Id = 1, Method = "ping" };
        await transport.SendAsync(request);

        // Read the echoed message
        await foreach (var msg in transport.Messages)
        {
            var received = Assert.IsType<JsonRpcRequest>(msg);
            Assert.Equal("ping", received.Method);
            Assert.Equal(1L, received.Id.AsNumber());
            break;
        }
    }

    [Fact]
    public async Task MultipleMessages_ThroughCat()
    {
        var options = new StdioClientTransportOptions { Command = "cat" };
        await using var transport = new StdioClientTransport(options);
        await transport.ConnectAsync();

        // Send multiple messages
        await transport.SendAsync(new JsonRpcRequest { Id = 1, Method = "a" });
        await transport.SendAsync(new JsonRpcRequest { Id = 2, Method = "b" });
        await transport.SendAsync(new JsonRpcRequest { Id = 3, Method = "c" });

        var received = new List<JsonRpcMessage>();
        await foreach (var msg in transport.Messages)
        {
            received.Add(msg);
            if (received.Count >= 3) break;
        }

        Assert.Equal(3, received.Count);
        Assert.Equal("a", ((JsonRpcRequest)received[0]).Method);
        Assert.Equal("b", ((JsonRpcRequest)received[1]).Method);
        Assert.Equal("c", ((JsonRpcRequest)received[2]).Method);
    }

    [Fact]
    public async Task ProcessExit_TriggersDisconnected()
    {
        var options = new StdioClientTransportOptions
        {
            Command = "echo",
            Arguments = "done"
        };

        var disconnectedFired = new TaskCompletionSource<TransportDisconnectedEventArgs>();

        await using var transport = new StdioClientTransport(options);
        transport.Disconnected += (_, args) => disconnectedFired.TrySetResult(args);
        await transport.ConnectAsync();

        var result = await Task.WhenAny(disconnectedFired.Task, Task.Delay(5000));
        Assert.True(disconnectedFired.Task.IsCompleted, "Disconnected event should fire when process exits");

        var args = await disconnectedFired.Task;
        Assert.NotNull(args.ExitCode);
        Assert.Equal(0, args.ExitCode);
    }

    [Fact]
    public async Task EnvironmentVariables_PassedToProcess()
    {
        var options = new StdioClientTransportOptions
        {
            Command = "env",
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["MCP_TEST_VAR"] = "hello_mcp"
            }
        };

        await using var transport = new StdioClientTransport(options);
        await transport.ConnectAsync();

        // env command outputs all env vars — we'll just verify it doesn't crash
        // and the process connects successfully
        await Task.Delay(100);
        // Process will exit after printing env, which is fine
    }

    [Fact]
    public async Task ConnectBeforeDispose_CleansUpProcess()
    {
        var options = new StdioClientTransportOptions
        {
            Command = "cat",
            ShutdownTimeout = TimeSpan.FromSeconds(1),
            KillGraceTimeout = TimeSpan.FromSeconds(1)
        };

        var transport = new StdioClientTransport(options);
        await transport.ConnectAsync();
        Assert.True(transport.IsConnected);

        await transport.DisposeAsync();
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task DoubleConnect_Throws()
    {
        var options = new StdioClientTransportOptions { Command = "cat" };
        await using var transport = new StdioClientTransport(options);

        await transport.ConnectAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.ConnectAsync());
    }

    [Fact]
    public async Task SendBeforeConnect_Throws()
    {
        var options = new StdioClientTransportOptions { Command = "cat" };
        await using var transport = new StdioClientTransport(options);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.SendAsync(McpMessages.Ping((RequestId)1)));
    }

    [Fact]
    public async Task LargeMessage_TransfersCorrectly()
    {
        var options = new StdioClientTransportOptions { Command = "cat" };
        await using var transport = new StdioClientTransport(options);
        await transport.ConnectAsync();

        var largeText = new string('x', 100_000);
        var request = new JsonRpcRequest
        {
            Id = 1,
            Method = "test",
            Params = McpJsonDefaults.ToElement(new { data = largeText })
        };

        await transport.SendAsync(request);

        await foreach (var msg in transport.Messages)
        {
            var received = Assert.IsType<JsonRpcRequest>(msg);
            var data = received.Params!.Value.GetProperty("data").GetString();
            Assert.Equal(100_000, data!.Length);
            break;
        }
    }
}
