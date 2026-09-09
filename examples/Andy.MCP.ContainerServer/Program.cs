using Andy.MCP.AspNetCore;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapMcp("/mcp", server => server.AddTool("echo", "Container MCP smoke test",
    (_, _) => Task.FromResult(CallToolResult.Text("Hello from the MCP container"))),
    new StreamableHttpServerOptions { AllowAnonymous = true });
app.Run();
