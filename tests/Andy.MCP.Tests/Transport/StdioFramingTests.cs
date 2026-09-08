using System.Text;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;
namespace Andy.MCP.Tests.Transport;

public class StdioFramingTests
{
    [Fact]
    public async Task Utf8Reader_AcceptsLiteralUnicode_AndKeepsEscapedNewlinesInsideOneMessage()
    {
        const string json = """{"jsonrpc":"2.0","id":1,"method":"vendor/é","params":{"text":"你好\n🙂"}}""";
        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(json + "\n" + json + "\n"));
        using var reader = StdioFraming.CreateReader(bytes);
        for (var i = 0; i < 2; i++)
        {
            var request = Assert.IsType<JsonRpcRequest>(McpJsonDefaults.Deserialize((await reader.ReadLineAsync())!));
            Assert.Equal("vendor/é", request.Method);
            Assert.Equal("你好\n🙂", request.Params!.Value.GetProperty("text").GetString());
        }
        Assert.Null(await reader.ReadLineAsync());
    }

    [Fact]
    public async Task Writer_EmitsUtf8WithoutBom_AndLfEvenWithWindowsWriterNewline()
    {
        using var bytes = new MemoryStream();
        await using var writer = StdioFraming.CreateWriter(bytes);
        writer.NewLine = "\r\n";
        const string json = """{"jsonrpc":"2.0","id":"é","result":{"text":"你好\n🙂"}}""";
        await StdioFraming.WriteAsync(writer, json, CancellationToken.None);
        await StdioFraming.WriteAsync(writer, json, CancellationToken.None);
        var data = bytes.ToArray();
        Assert.False(data.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        Assert.DoesNotContain((byte)'\r', data);
        Assert.Equal(json + "\n" + json + "\n", new UTF8Encoding(false, true).GetString(data));
        Assert.Equal(2, data.Count(b => b == (byte)'\n'));
    }

    [Fact]
    public async Task Reader_RejectsInvalidUtf8InsteadOfReplacingProtocolText()
    {
        using var bytes = new MemoryStream([0xff, 0x0a]);
        using var reader = StdioFraming.CreateReader(bytes);
        await Assert.ThrowsAsync<DecoderFallbackException>(async () => await reader.ReadLineAsync());
    }
}
