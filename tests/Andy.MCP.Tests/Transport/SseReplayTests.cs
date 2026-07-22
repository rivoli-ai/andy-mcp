using System.Collections.Concurrent;
using Andy.MCP.AspNetCore;
using Andy.MCP.Protocol;

namespace Andy.MCP.Tests;

/// <summary>
/// Tests SSE replay, Last-Event-ID resumption, and multi-stream exactly-once delivery for
/// Streamable HTTP sessions (issues #44/#73). Server events are buffered and each is claimed by
/// exactly one stream, with per-stream event ids for resumption.
/// </summary>
public class SseReplayTests
{
    private static JsonRpcNotification Notif(string text) => new()
    {
        Method = "notifications/message",
        Params = McpJsonDefaults.ToElement(new { text })
    };

    private static string TextOf(JsonRpcMessage message) =>
        ((JsonRpcNotification)message).Params!.Value.GetProperty("text").GetString()!;

    private static async Task<List<(long Seq, JsonRpcMessage Message)>> ReadUpToAsync(
        StreamableHttpSession session, string streamId, long resumeAfterSeq, int count)
    {
        var list = new List<(long, JsonRpcMessage)>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var entry in session.ReadServerEventsAsync(streamId, resumeAfterSeq, cts.Token))
        {
            list.Add(entry);
            if (list.Count >= count)
                break;
        }
        return list;
    }

    [Fact]
    public async Task Stream_ClaimsAllBufferedEvents()
    {
        var session = new StreamableHttpSession("s1");
        await session.SendAsync(Notif("a"));
        await session.SendAsync(Notif("b"));
        await session.SendAsync(Notif("c"));

        var events = await ReadUpToAsync(session, "stream-1", resumeAfterSeq: 0, count: 3);

        Assert.Equal(new[] { "a", "b", "c" }, events.Select(e => TextOf(e.Message)));
        Assert.Equal(new[] { 1L, 2L, 3L }, events.Select(e => e.Seq));
    }

    [Fact]
    public async Task Resume_ReplaysOnlyTheStreamsOwnLaterEvents()
    {
        var session = new StreamableHttpSession("s1");
        await session.SendAsync(Notif("a"));
        await session.SendAsync(Notif("b"));
        await session.SendAsync(Notif("c"));

        // Stream delivers a,b,c (claiming them).
        await ReadUpToAsync(session, "stream-1", resumeAfterSeq: 0, count: 3);

        // Reconnect as the same stream, last seen seq 1 ("a") -> replay "b","c".
        var resumed = await ReadUpToAsync(session, "stream-1", resumeAfterSeq: 1, count: 2);
        Assert.Equal(new[] { "b", "c" }, resumed.Select(e => TextOf(e.Message)));
    }

    [Fact]
    public async Task LiveEvent_WakesWaitingStream()
    {
        var session = new StreamableHttpSession("s1");

        var readerTask = ReadUpToAsync(session, "stream-1", resumeAfterSeq: 0, count: 1);
        await Task.Delay(50); // waiting with nothing buffered
        await session.SendAsync(Notif("live"));

        var events = await readerTask;
        Assert.Equal("live", TextOf(Assert.Single(events).Message));
    }

    [Fact]
    public async Task MultipleStreams_EachEventDeliveredExactlyOnce()
    {
        var session = new StreamableHttpSession("s1");
        const int total = 8;
        for (var i = 0; i < total; i++)
            await session.SendAsync(Notif($"n{i}"));

        var delivered = new ConcurrentBag<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        async Task Drain(string streamId)
        {
            await foreach (var (_, message) in session.ReadServerEventsAsync(streamId, 0, cts.Token))
            {
                delivered.Add(TextOf(message));
                if (delivered.Count >= total)
                    cts.Cancel();
            }
        }

        var s1 = Drain("stream-1");
        var s2 = Drain("stream-2");
        try { await Task.WhenAll(s1, s2); } catch (OperationCanceledException) { }

        var all = delivered.ToList();
        Assert.Equal(total, all.Count);              // no duplicates -> exactly `total` deliveries
        Assert.Equal(total, all.Distinct().Count()); // every message exactly once
    }

    [Fact]
    public void EventId_RoundTrips_AndRejectsMalformed()
    {
        var id = StreamableHttpSession.FormatEventId("stream-abc", 7);
        Assert.True(StreamableHttpSession.TryParseEventId(id, out var streamId, out var seq));
        Assert.Equal("stream-abc", streamId);
        Assert.Equal(7, seq);

        Assert.False(StreamableHttpSession.TryParseEventId("garbage", out _, out _));
        Assert.False(StreamableHttpSession.TryParseEventId(null, out _, out _));
    }

    [Fact]
    public async Task ClosedSession_EndsStream()
    {
        var session = new StreamableHttpSession("s1");
        await session.SendAsync(Notif("a"));
        session.Close();

        var events = new List<JsonRpcMessage>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var (_, message) in session.ReadServerEventsAsync("stream-1", 0, cts.Token))
            events.Add(message);

        Assert.Equal("a", TextOf(Assert.Single(events)));
    }
}
