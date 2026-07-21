using System.Text;

namespace Andy.MCP.Transport.Sse;

/// <summary>
/// Writes Server-Sent Events to a stream.
/// </summary>
public sealed class SseWriter
{
    private readonly Stream _stream;
    private readonly Encoding _encoding = Encoding.UTF8;

    public SseWriter(Stream stream)
    {
        _stream = stream;
    }

    /// <summary>
    /// Write a complete SSE event and flush.
    /// </summary>
    public async Task WriteEventAsync(SseEvent evt, CancellationToken cancellationToken = default)
    {
        // SSE framing requires LF ("\n") line endings; do not use AppendLine, which emits the
        // platform newline (CRLF on Windows) and breaks the wire format.
        var sb = new StringBuilder();

        if (evt.EventType != "message")
            sb.Append("event: ").Append(evt.EventType).Append('\n');

        // Split data into lines (SSE requires each line to be prefixed with "data: ").
        var dataLines = evt.Data.Split('\n');
        foreach (var line in dataLines)
            sb.Append("data: ").Append(line.TrimEnd('\r')).Append('\n');

        if (evt.Id is not null)
            sb.Append("id: ").Append(evt.Id).Append('\n');

        if (evt.Retry is not null)
            sb.Append("retry: ").Append(evt.Retry.Value).Append('\n');

        // Blank line terminates the event.
        sb.Append('\n');

        var bytes = _encoding.GetBytes(sb.ToString());
        await _stream.WriteAsync(bytes, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Write a keep-alive comment.
    /// </summary>
    public async Task WriteCommentAsync(string comment = "", CancellationToken cancellationToken = default)
    {
        var bytes = _encoding.GetBytes($": {comment}\n\n");
        await _stream.WriteAsync(bytes, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }
}
