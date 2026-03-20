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
        var sb = new StringBuilder();

        if (evt.EventType != "message")
            sb.Append("event: ").AppendLine(evt.EventType);

        // Split data into lines (SSE requires each line to be prefixed with "data: ")
        var dataLines = evt.Data.Split('\n');
        foreach (var line in dataLines)
            sb.Append("data: ").AppendLine(line);

        if (evt.Id is not null)
            sb.Append("id: ").AppendLine(evt.Id);

        if (evt.Retry is not null)
            sb.Append("retry: ").Append(evt.Retry.Value).AppendLine();

        // Blank line terminates the event
        sb.AppendLine();

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
