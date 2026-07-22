using Andy.MCP.AspNetCore;
using Andy.MCP.Protocol;

namespace Andy.MCP.Tests;

/// <summary>
/// Tests SSE replay and Last-Event-ID resumption for Streamable HTTP sessions (issue #44): server
/// events are buffered with stream-identity-encoded ids so a reconnecting client resumes cleanly,
/// and an id from a different stream never replays here.
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
        StreamableHttpSession session, long afterSeq, int count)
    {
        var list = new List<(long, JsonRpcMessage)>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var entry in session.ReadServerEventsAsync(afterSeq, cts.Token))
        {
            list.Add(entry);
            if (list.Count >= count)
                break;
        }
        return list;
    }

    [Fact]
    public async Task Replay_FromStart_ReturnsAllBufferedEvents()
    {
        var session = new StreamableHttpSession("s1");
        await session.SendAsync(Notif("a"));
        await session.SendAsync(Notif("b"));
        await session.SendAsync(Notif("c"));

        var events = await ReadUpToAsync(session, afterSeq: 0, count: 3);

        Assert.Equal(new[] { "a", "b", "c" }, events.Select(e => TextOf(e.Message)));
        Assert.Equal(new[] { 1L, 2L, 3L }, events.Select(e => e.Seq));
    }

    [Fact]
    public async Task Resume_AfterLastEventId_ReplaysOnlyLaterEvents()
    {
        var session = new StreamableHttpSession("s1");
        await session.SendAsync(Notif("a"));
        await session.SendAsync(Notif("b"));
        await session.SendAsync(Notif("c"));

        // Client last saw event seq 1 ("a") -> resume yields "b", "c".
        var events = await ReadUpToAsync(session, afterSeq: 1, count: 2);

        Assert.Equal(new[] { "b", "c" }, events.Select(e => TextOf(e.Message)));
    }

    [Fact]
    public async Task LiveEvent_WakesWaitingReader()
    {
        var session = new StreamableHttpSession("s1");

        var readerTask = ReadUpToAsync(session, afterSeq: session.CurrentSequence, count: 1);
        await Task.Delay(50); // reader is now waiting with nothing buffered
        await session.SendAsync(Notif("live"));

        var events = await readerTask;
        Assert.Equal("live", TextOf(Assert.Single(events).Message));
    }

    [Fact]
    public void EventId_RoundTrips_AndRejectsForeignStream()
    {
        var a = new StreamableHttpSession("s1");
        var b = new StreamableHttpSession("s2");

        var id = a.FormatEventId(7);
        Assert.True(a.TryParseEventId(id, out var seq));
        Assert.Equal(7, seq);

        // An id minted on session A must not resume session B's stream.
        Assert.False(b.TryParseEventId(id, out _));
        Assert.False(a.TryParseEventId("garbage", out _));
        Assert.False(a.TryParseEventId(null, out _));
    }

    [Fact]
    public async Task ReplayBuffer_IsBounded()
    {
        var session = new StreamableHttpSession("s1");
        for (var i = 0; i < 300; i++)
            await session.SendAsync(Notif($"n{i}"));

        // Reading from the very start yields at most the retained window, ending with the newest.
        var events = await ReadUpToAsync(session, afterSeq: 0, count: 256);

        Assert.Equal(256, events.Count);
        Assert.Equal("n299", TextOf(events[^1].Message)); // newest retained
        Assert.True(events[0].Seq > 1);                    // oldest events were trimmed
    }

    [Fact]
    public async Task ClosedSession_EndsReplayStream()
    {
        var session = new StreamableHttpSession("s1");
        await session.SendAsync(Notif("a"));

        session.Close();

        var events = new List<JsonRpcMessage>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var (_, message) in session.ReadServerEventsAsync(0, cts.Token))
            events.Add(message);

        // Buffered "a" is still replayable, then the stream completes because the session closed.
        Assert.Equal("a", TextOf(Assert.Single(events)));
    }
}
