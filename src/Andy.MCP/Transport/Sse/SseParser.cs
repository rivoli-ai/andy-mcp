using System.Runtime.CompilerServices;
using System.Text;

namespace Andy.MCP.Transport.Sse;

/// <summary>
/// Streaming parser for the Server-Sent Events (SSE) protocol.
/// Reads from a stream and yields parsed SseEvent objects.
/// Handles multi-line data fields, event types, IDs, retry, and BOM stripping.
/// </summary>
public static class SseParser
{
    /// <summary>
    /// Parse SSE events from a stream. Each event is delimited by a blank line.
    /// </summary>
    public static async IAsyncEnumerable<SseEvent> ParseAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);

        string? eventType = null;
        var dataLines = new List<string>();
        string? id = null;
        int? retry = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) // EOF
            {
                // Emit any pending event
                if (dataLines.Count > 0)
                    yield return BuildEvent(eventType, dataLines, id, retry);
                yield break;
            }

            // Strip BOM from first line if present
            if (line.Length > 0 && line[0] == '\uFEFF')
                line = line[1..];

            // Blank line = event boundary
            if (line.Length == 0)
            {
                if (dataLines.Count > 0)
                {
                    yield return BuildEvent(eventType, dataLines, id, retry);
                    eventType = null;
                    dataLines.Clear();
                    // id persists across events (Last-Event-ID)
                    retry = null;
                }
                continue;
            }

            // Comment lines start with ':'
            if (line[0] == ':')
                continue;

            // Parse field
            var colonIndex = line.IndexOf(':');
            string field;
            string value;

            if (colonIndex == -1)
            {
                // Field with no value
                field = line;
                value = "";
            }
            else
            {
                field = line[..colonIndex];
                // Skip single space after colon if present
                value = colonIndex + 1 < line.Length && line[colonIndex + 1] == ' '
                    ? line[(colonIndex + 2)..]
                    : line[(colonIndex + 1)..];
            }

            switch (field)
            {
                case "event":
                    eventType = value;
                    break;
                case "data":
                    dataLines.Add(value);
                    break;
                case "id":
                    // ID must not contain null character
                    if (!value.Contains('\0'))
                        id = value;
                    break;
                case "retry":
                    if (int.TryParse(value, out var retryMs))
                        retry = retryMs;
                    break;
                    // Unknown fields are ignored per spec
            }
        }
    }

    private static SseEvent BuildEvent(string? eventType, List<string> dataLines, string? id, int? retry)
    {
        return new SseEvent
        {
            EventType = eventType ?? "message",
            Data = string.Join("\n", dataLines),
            Id = id,
            Retry = retry
        };
    }
}
