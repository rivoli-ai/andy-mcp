using System.Text;
namespace Andy.MCP.Transport;

internal static class StdioFraming
{
    internal static StreamReader CreateReader(Stream stream) => new(stream, new UTF8Encoding(false, true),
        detectEncodingFromByteOrderMarks: false, leaveOpen: true);
    internal static StreamWriter CreateWriter(Stream stream) => new(stream, new UTF8Encoding(false, true), leaveOpen: true);
    internal static async Task WriteAsync(TextWriter writer, string json, CancellationToken ct)
    {
        await writer.WriteAsync((json + '\n').AsMemory(), ct);
        await writer.FlushAsync(ct);
    }
}
