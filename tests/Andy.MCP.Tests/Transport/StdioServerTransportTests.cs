using System.Text.Json;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;

namespace Andy.MCP.Tests.Transport;

public class StdioServerTransportTests
{
    [Fact]
    public async Task MalformedInput_WritesJsonRpcParseError()
    {
        using var input = new StringReader("this is not valid json\n");
        using var output = new StringWriter();

        await using var transport = new StdioServerTransport(input, output);
        await transport.StartAsync();

        // The malformed line is processed once, then stdin EOFs; give the write loop a moment.
        await Task.Delay(100);

        var written = output.ToString().Trim();
        Assert.NotEmpty(written);

        using var doc = JsonDocument.Parse(written);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("id").ValueKind);
        Assert.Equal(McpErrorCodes.ParseError, root.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ReceiveMessage_FromStdin()
    {
        var request = new JsonRpcRequest { Id = 1, Method = "ping" };
        var json = McpJsonDefaults.Serialize(request);

        using var input = new StringReader(json + "\n");
        using var output = new StringWriter();

        await using var transport = new StdioServerTransport(input, output);
        await transport.StartAsync();

        var messages = new List<JsonRpcMessage>();
        await foreach (var msg in transport.Messages)
        {
            messages.Add(msg);
            break; // Just get the first one
        }

        var received = Assert.IsType<JsonRpcRequest>(messages[0]);
        Assert.Equal("ping", received.Method);
        Assert.Equal(1L, received.Id.AsNumber());
    }

    [Fact]
    public async Task SendMessage_WritesToStdout()
    {
        // Use a blocking reader so stdin doesn't EOF immediately
        var blockingReader = new BlockingReader();
        using var output = new StringWriter();

        await using var transport = new StdioServerTransport(blockingReader, output);
        await transport.StartAsync();

        var response = JsonRpcResponse.Success((RequestId)1);
        await transport.SendAsync(response);

        // Give write loop a moment to process
        await Task.Delay(50);

        var written = output.ToString().Trim();
        Assert.NotEmpty(written);

        var parsed = Assert.IsType<JsonRpcResponse>(McpJsonDefaults.Deserialize(written));
        Assert.True(parsed.IsSuccess);
        Assert.Equal(1L, parsed.Id.AsNumber());

        blockingReader.Release();
    }

    [Fact]
    public async Task MultipleMessages_ReceivedInOrder()
    {
        var lines = string.Join("\n",
            McpJsonDefaults.Serialize(new JsonRpcRequest { Id = 1, Method = "a" }),
            McpJsonDefaults.Serialize(new JsonRpcRequest { Id = 2, Method = "b" }),
            McpJsonDefaults.Serialize(new JsonRpcRequest { Id = 3, Method = "c" }),
            ""); // trailing newline

        using var input = new StringReader(lines);
        using var output = new StringWriter();

        await using var transport = new StdioServerTransport(input, output);
        await transport.StartAsync();

        var messages = new List<JsonRpcMessage>();
        await foreach (var msg in transport.Messages)
        {
            messages.Add(msg);
        }

        Assert.Equal(3, messages.Count);
        Assert.Equal("a", ((JsonRpcRequest)messages[0]).Method);
        Assert.Equal("b", ((JsonRpcRequest)messages[1]).Method);
        Assert.Equal("c", ((JsonRpcRequest)messages[2]).Method);
    }

    [Fact]
    public async Task EmptyLines_Skipped()
    {
        var lines = "\n\n" +
            McpJsonDefaults.Serialize(new JsonRpcRequest { Id = 1, Method = "test" }) +
            "\n\n";

        using var input = new StringReader(lines);
        using var output = new StringWriter();

        await using var transport = new StdioServerTransport(input, output);
        await transport.StartAsync();

        var messages = new List<JsonRpcMessage>();
        await foreach (var msg in transport.Messages)
        {
            messages.Add(msg);
        }

        Assert.Single(messages);
    }

    [Fact]
    public async Task InvalidJson_SkippedWithoutCrash()
    {
        var lines = string.Join("\n",
            "this is not json",
            McpJsonDefaults.Serialize(new JsonRpcRequest { Id = 1, Method = "valid" }),
            "{bad json here}",
            "");

        using var input = new StringReader(lines);
        using var output = new StringWriter();

        await using var transport = new StdioServerTransport(input, output);
        await transport.StartAsync();

        var messages = new List<JsonRpcMessage>();
        await foreach (var msg in transport.Messages)
        {
            messages.Add(msg);
        }

        // Only the valid message should come through
        Assert.Single(messages);
        Assert.Equal("valid", ((JsonRpcRequest)messages[0]).Method);
    }

    [Fact]
    public async Task StdinEof_TriggersDisconnected()
    {
        using var input = new StringReader(""); // EOF immediately
        using var output = new StringWriter();

        var disconnectedFired = new TaskCompletionSource();

        await using var transport = new StdioServerTransport(input, output);
        transport.Disconnected += (_, args) =>
        {
            Assert.Equal("stdin closed", args.Reason);
            disconnectedFired.TrySetResult();
        };
        await transport.StartAsync();

        await Task.WhenAny(disconnectedFired.Task, Task.Delay(2000));
        Assert.True(disconnectedFired.Task.IsCompleted);
    }

    [Fact]
    public async Task Notification_ReceivedCorrectly()
    {
        var notification = new JsonRpcNotification
        {
            Method = "notifications/tools/list_changed"
        };
        var json = McpJsonDefaults.Serialize(notification) + "\n";

        using var input = new StringReader(json);
        using var output = new StringWriter();

        await using var transport = new StdioServerTransport(input, output);
        await transport.StartAsync();

        var messages = new List<JsonRpcMessage>();
        await foreach (var msg in transport.Messages)
        {
            messages.Add(msg);
        }

        var received = Assert.IsType<JsonRpcNotification>(messages[0]);
        Assert.Equal("notifications/tools/list_changed", received.Method);
    }

    [Fact]
    public async Task IsConnected_ReflectsState()
    {
        var blockingReader = new BlockingReader();
        using var output = new StringWriter();

        await using var transport = new StdioServerTransport(blockingReader, output);

        Assert.False(transport.IsConnected);
        await transport.StartAsync();
        Assert.True(transport.IsConnected);

        // Release stdin (EOF) → should disconnect
        blockingReader.Release();
        await Task.Delay(100);
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task DoubleStart_Throws()
    {
        var blockingReader = new BlockingReader();
        using var output = new StringWriter();

        await using var transport = new StdioServerTransport(blockingReader, output);
        await transport.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.StartAsync());
        blockingReader.Release();
    }

    [Fact]
    public async Task SendBeforeStart_Throws()
    {
        using var input = new StringReader("");
        using var output = new StringWriter();

        await using var transport = new StdioServerTransport(input, output);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.SendAsync(McpMessages.Ping((RequestId)1)));
    }

    /// <summary>
    /// A TextReader that blocks on ReadLineAsync until Release() is called.
    /// Prevents stdin EOF from closing the transport prematurely during tests.
    /// </summary>
    private sealed class BlockingReader : TextReader
    {
        private readonly TaskCompletionSource _released = new();

        public void Release() => _released.TrySetResult();

        public override string? ReadLine() => null;

        public override Task<string?> ReadLineAsync()
        {
            return _released.Task.ContinueWith<string?>(_ => null);
        }

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            return new ValueTask<string?>(
                _released.Task.ContinueWith<string?>(_ => null, cancellationToken));
        }
    }
}
