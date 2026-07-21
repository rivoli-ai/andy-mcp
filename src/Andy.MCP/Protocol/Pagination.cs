using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.MCP.Protocol;

/// <summary>
/// Base type for paginated request params. Includes an optional cursor to continue from a previous page.
/// </summary>
public record PaginatedRequest
{
    [JsonPropertyName("cursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Cursor { get; init; }

    /// <summary>Reserved protocol metadata (_meta), preserved round-trip.</summary>
    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; init; }
}

/// <summary>
/// Base type for paginated response results. Includes an optional nextCursor if more results exist.
/// </summary>
public record PaginatedResult
{
    [JsonPropertyName("nextCursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextCursor { get; init; }

    /// <summary>Reserved protocol metadata (_meta), preserved round-trip.</summary>
    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; init; }
}

/// <summary>
/// Server-side pagination helper. Generates session-bound opaque cursors and slices collections.
/// </summary>
public sealed class PaginationHelper
{
    private readonly string _sessionKey;
    private readonly int _defaultPageSize;

    /// <param name="sessionKey">A session-specific secret used to HMAC-sign cursors, preventing cross-session reuse.</param>
    /// <param name="defaultPageSize">Default number of items per page.</param>
    public PaginationHelper(string sessionKey, int defaultPageSize = 50)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(defaultPageSize, 1);

        _sessionKey = sessionKey;
        _defaultPageSize = defaultPageSize;
    }

    /// <summary>
    /// Get a page of items from the full list, using the provided cursor to determine the starting offset.
    /// </summary>
    /// <returns>The page of items and an optional nextCursor if more items remain.</returns>
    public PaginatedSlice<T> GetPage<T>(IReadOnlyList<T> items, string? cursor, int? pageSize = null)
    {
        var size = pageSize ?? _defaultPageSize;
        var offset = 0;

        if (cursor is not null)
        {
            offset = DecodeCursor(cursor);
        }

        if (offset < 0 || offset > items.Count)
        {
            throw new McpPaginationException($"Invalid cursor: offset {offset} is out of range for collection of {items.Count} items.");
        }

        var remaining = items.Count - offset;
        var count = Math.Min(size, remaining);
        var page = new List<T>(count);

        for (int i = offset; i < offset + count; i++)
        {
            page.Add(items[i]);
        }

        var nextOffset = offset + count;
        string? nextCursor = nextOffset < items.Count ? EncodeCursor(nextOffset) : null;

        return new PaginatedSlice<T>(page, nextCursor);
    }

    private string EncodeCursor(int offset)
    {
        var payload = JsonSerializer.Serialize(new CursorPayload { Offset = offset });
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hmac = ComputeHmac(payloadBytes);

        // Format: base64(payload) . base64(hmac)
        var payloadB64 = Convert.ToBase64String(payloadBytes);
        var hmacB64 = Convert.ToBase64String(hmac);
        return $"{payloadB64}.{hmacB64}";
    }

    private int DecodeCursor(string cursor)
    {
        var parts = cursor.Split('.');
        if (parts.Length != 2)
            throw new McpPaginationException("Invalid cursor format.");

        byte[] payloadBytes;
        byte[] receivedHmac;
        try
        {
            payloadBytes = Convert.FromBase64String(parts[0]);
            receivedHmac = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            throw new McpPaginationException("Invalid cursor: malformed encoding.");
        }

        var expectedHmac = ComputeHmac(payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(expectedHmac, receivedHmac))
            throw new McpPaginationException("Invalid cursor: signature mismatch (possibly from a different session).");

        try
        {
            var payload = JsonSerializer.Deserialize<CursorPayload>(payloadBytes);
            return payload?.Offset ?? throw new McpPaginationException("Invalid cursor: missing offset.");
        }
        catch (JsonException)
        {
            throw new McpPaginationException("Invalid cursor: corrupted payload.");
        }
    }

    private byte[] ComputeHmac(byte[] data)
    {
        var keyBytes = Encoding.UTF8.GetBytes(_sessionKey);
        return HMACSHA256.HashData(keyBytes, data);
    }

    private sealed record CursorPayload
    {
        [JsonPropertyName("o")]
        public int Offset { get; init; }
    }
}

/// <summary>
/// Result of a pagination operation: a page of items and an optional next cursor.
/// </summary>
public sealed record PaginatedSlice<T>(IReadOnlyList<T> Items, string? NextCursor)
{
    public bool HasMore => NextCursor is not null;
}

/// <summary>
/// Thrown when a cursor is invalid, tampered, or from a different session.
/// Callers should convert this to a JSON-RPC error with code -32602 (INVALID_PARAMS).
/// </summary>
public class McpPaginationException : Exception
{
    public McpPaginationException(string message) : base(message) { }
}

/// <summary>
/// Client-side helper that auto-follows pagination cursors as an async enumerable.
/// </summary>
public static class PaginationExtensions
{
    /// <summary>
    /// Iterate through all pages by auto-following nextCursor until exhausted.
    /// </summary>
    /// <param name="fetcher">A function that takes an optional cursor and returns a page of items + nextCursor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async IAsyncEnumerable<T> PaginateAllAsync<T>(
        Func<string?, CancellationToken, Task<(IReadOnlyList<T> items, string? nextCursor)>> fetcher,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? cursor = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (items, nextCursor) = await fetcher(cursor, cancellationToken);

            foreach (var item in items)
            {
                yield return item;
            }

            cursor = nextCursor;
        } while (cursor is not null);
    }
}
