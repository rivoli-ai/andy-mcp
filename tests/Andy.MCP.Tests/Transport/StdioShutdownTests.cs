using System.Diagnostics;
using Andy.MCP.Transport;

namespace Andy.MCP.Tests.Transport;

public class StdioShutdownTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnixShutdown_AllowsTermHandler_ThenKillsAnIgnoringChild(bool ignoreTerm)
    {
        if (OperatingSystem.IsWindows()) return; // SIGTERM is a Unix-only step.
        var folder = Path.Combine(Path.GetTempPath(), "mcp-shutdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var script = Path.Combine(folder, "child.sh");
        var marker = Path.Combine(folder, "terminated");
        var ready = Path.Combine(folder, "ready");
        await File.WriteAllTextAsync(script, (ignoreTerm ? "trap '' TERM\n" :
            "trap 'printf terminated > \"$MCP_TEST_MARKER\"; exit 0' TERM\n") +
            "printf ready > \"$MCP_TEST_READY\"\nwhile :; do sleep 0.05; done\n");
        try
        {
            await using var transport = new StdioClientTransport(new()
            {
                Command = "/bin/sh",
                Arguments = "\"" + script + "\"",
                EnvironmentVariables = new Dictionary<string, string> { ["MCP_TEST_MARKER"] = marker, ["MCP_TEST_READY"] = ready },
                ShutdownTimeout = TimeSpan.FromMilliseconds(100),
                KillGraceTimeout = TimeSpan.FromSeconds(1)
            });
            await transport.ConnectAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(ready)) await Task.Delay(10, timeout.Token);
            var elapsed = Stopwatch.StartNew();
            await transport.DisposeAsync();
            Assert.False(transport.IsConnected);
            Assert.Equal(!ignoreTerm, File.Exists(marker));
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
