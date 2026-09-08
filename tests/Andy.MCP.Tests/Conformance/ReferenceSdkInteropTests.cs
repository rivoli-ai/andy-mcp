using System.Diagnostics;
using Andy.MCP.AspNetCore;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;
using Andy.MCP.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Andy.MCP.Tests.Conformance;

[Trait("Category", "Interop")]
public class ReferenceSdkInteropTests
{
    private static string Root
    {
        get
        {
            for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
                if (File.Exists(Path.Combine(path.FullName, "tests", "interop", "package.json"))) return path.FullName;
            throw new InvalidOperationException("Run interop from a repository checkout after npm ci --prefix tests/interop.");
        }
    }
    private static string Script(string name) => Path.Combine(Root, "tests", "interop", name + ".mjs");
    private sealed class Node : IAsyncDisposable
    {
        public Process Process { get; }
        public Task<string> Errors { get; }
        public Node(params string[] args)
        {
            var info = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = Root };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            Process = Process.Start(info) ?? throw new InvalidOperationException("Node is required; install it and run npm ci --prefix tests/interop.");
            Errors = Process.StandardError.ReadToEndAsync();
        }
        public async ValueTask DisposeAsync()
        {
            if (!Process.HasExited) Process.Kill(entireProcessTree: true);
            await Process.WaitForExitAsync();
            Process.Dispose();
        }
    }
    private sealed class Sampler : ISamplingHandler
    {
        public Task<CreateMessageResult> HandleAsync(CreateMessageRequest request, CancellationToken ct) => Task.FromResult(new CreateMessageResult
        { Role = Role.Assistant, Model = "andy", Content = [new TextContent("sampled by Andy")] });
    }
    [Theory]
    [InlineData("stdio")]
    [InlineData("json")]
    [InlineData("sse")]
    public async Task AndyClient_UsesOfficialSdkServer(string mode)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Node? node = null;
        try
        {
            IClientTransport transport;
            if (mode == "stdio") transport = new StdioClientTransport(new() { Command = "node", Arguments = $"\"{Script("reference-server")}\" stdio" });
            else
            {
                node = new Node(Script("reference-server"), mode);
                var endpoint = await node.Process.StandardOutput.ReadLineAsync(timeout.Token);
                if (endpoint is null) Assert.Fail(await node.Errors.WaitAsync(TimeSpan.FromSeconds(1)));
                transport = new StreamableHttpClientTransport(new() { Endpoint = new Uri(endpoint!), EnableServerSseStream = true });
            }
            await using var client = await McpClient.ConnectAsync(transport, new McpClientOptions { SamplingHandler = new Sampler() }, cancellationToken: timeout.Token);
            Assert.Equal("pinned-official-sdk", client.Session.RemoteInfo!.Name);
            await client.PingAsync(timeout.Token);
            Assert.Contains(await client.ListToolsAsync(timeout.Token), t => t.Name == "echo");
            var echo = await client.CallToolAsync("echo", new { message = "世界\ninterop" }, timeout.Token);
            Assert.Equal("世界\ninterop", Assert.IsType<TextContent>(echo.Content[0]).Text);
            Assert.True(echo.Meta!.Value.GetProperty("reference").GetBoolean());
            Assert.Single((await client.ReadResourceAsync((await client.ListResourcesAsync(timeout.Token))[0].Uri, timeout.Token)).Contents);
            Assert.Single((await client.GetPromptAsync((await client.ListPromptsAsync(timeout.Token))[0].Name, ct: timeout.Token)).Messages);
            var sample = await client.CallToolAsync("sample", new { }, timeout.Token);
            Assert.Equal("sampled by Andy", Assert.IsType<TextContent>(sample.Content[0]).Text);
        }
        finally { if (node is not null) await node.DisposeAsync(); }
    }
    [Fact]
    public async Task OfficialSdkClient_UsesDocumentedStdioServer()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var dll = Path.Combine(Root, "examples", "Andy.MCP.Examples", "bin", configuration, "net10.0", "Andy.MCP.Examples.dll");
        Assert.True(File.Exists(dll), "Build the solution before interop.");
        await RunClient("stdio", dll, timeout.Token);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfficialSdkClient_UsesAndyHttpServer(bool sse)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapMcp("/mcp", server =>
        {
            server.AddTool("greet", "greet", (args, _) => Task.FromResult(CallToolResult.Text("Hello " + args!.Value.GetProperty("name").GetString())));
            server.AddTool("sample", "sample", async (_, ct) => new CallToolResult
            {
                Content = (await server.CreateMessageAsync(new CreateMessageRequest
                { Messages = [new SamplingMessage { Role = Role.User, Content = [new TextContent("sample")] }], MaxTokens = 10 }, ct)).Content
            });
            server.AddResource("info://server", "info", (uri, _) => Task.FromResult<ResourceContents>(new TextResourceContents { Uri = uri, Text = "info" }));
            server.AddPrompt("explain", "explain", (_, _, _) => Task.FromResult(new GetPromptResult
            { Messages = [new PromptMessage { Role = Role.User, Content = new TextContent("explain") }] }));
        }, new StreamableHttpServerOptions { AllowAnonymous = true, UseSseResponses = sse });
        await app.StartAsync(timeout.Token);
        await RunClient("http", app.Urls.Single() + "/mcp", timeout.Token);
        await app.StopAsync(timeout.Token);
    }
    private static async Task RunClient(string mode, string target, CancellationToken ct)
    {
        await using var node = new Node(Script("reference-client"), mode, target);
        var output = node.Process.StandardOutput.ReadToEndAsync(ct);
        await node.Process.WaitForExitAsync(ct);
        Assert.True(node.Process.ExitCode == 0, await node.Errors);
        Assert.Contains("REFERENCE_CLIENT_PASS", await output);
    }
}
