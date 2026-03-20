using System.Text;
using Andy.MCP.Transport.Sse;

namespace Andy.MCP.Tests.Transport;

public class SseParserTests
{
    private static Stream ToStream(string text) =>
        new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task ParsesSimpleEvent()
    {
        var stream = ToStream("data: hello world\n\n");
        var events = await Collect(stream);

        Assert.Single(events);
        Assert.Equal("hello world", events[0].Data);
        Assert.Equal("message", events[0].EventType);
    }

    [Fact]
    public async Task ParsesMultiLineData()
    {
        var stream = ToStream("data: line1\ndata: line2\ndata: line3\n\n");
        var events = await Collect(stream);

        Assert.Single(events);
        Assert.Equal("line1\nline2\nline3", events[0].Data);
    }

    [Fact]
    public async Task ParsesEventType()
    {
        var stream = ToStream("event: custom\ndata: payload\n\n");
        var events = await Collect(stream);

        Assert.Single(events);
        Assert.Equal("custom", events[0].EventType);
        Assert.Equal("payload", events[0].Data);
    }

    [Fact]
    public async Task ParsesEventId()
    {
        var stream = ToStream("id: 42\ndata: test\n\n");
        var events = await Collect(stream);

        Assert.Single(events);
        Assert.Equal("42", events[0].Id);
    }

    [Fact]
    public async Task ParsesRetry()
    {
        var stream = ToStream("retry: 3000\ndata: test\n\n");
        var events = await Collect(stream);

        Assert.Single(events);
        Assert.Equal(3000, events[0].Retry);
    }

    [Fact]
    public async Task IgnoresComments()
    {
        var stream = ToStream(": this is a comment\ndata: actual data\n\n");
        var events = await Collect(stream);

        Assert.Single(events);
        Assert.Equal("actual data", events[0].Data);
    }

    [Fact]
    public async Task MultipleEvents()
    {
        var stream = ToStream("data: first\n\ndata: second\n\ndata: third\n\n");
        var events = await Collect(stream);

        Assert.Equal(3, events.Count);
        Assert.Equal("first", events[0].Data);
        Assert.Equal("second", events[1].Data);
        Assert.Equal("third", events[2].Data);
    }

    [Fact]
    public async Task SkipsEmptyDataEvents()
    {
        // Blank line without any data fields — no event emitted
        var stream = ToStream("\n\ndata: real\n\n");
        var events = await Collect(stream);

        Assert.Single(events);
        Assert.Equal("real", events[0].Data);
    }

    [Fact]
    public async Task IdPersistsAcrossEvents()
    {
        var stream = ToStream("id: 1\ndata: first\n\ndata: second\n\n");
        var events = await Collect(stream);

        Assert.Equal(2, events.Count);
        Assert.Equal("1", events[0].Id);
        Assert.Equal("1", events[1].Id); // ID persists
    }

    [Fact]
    public async Task SpaceAfterColonStripped()
    {
        var stream = ToStream("data: with space\n\n");
        var events = await Collect(stream);
        Assert.Equal("with space", events[0].Data);
    }

    [Fact]
    public async Task NoSpaceAfterColon()
    {
        var stream = ToStream("data:no space\n\n");
        var events = await Collect(stream);
        Assert.Equal("no space", events[0].Data);
    }

    [Fact]
    public async Task FieldWithNoValue()
    {
        var stream = ToStream("data\n\n");
        var events = await Collect(stream);
        Assert.Equal("", events[0].Data);
    }

    [Fact]
    public async Task EmptyStream_NoEvents()
    {
        var stream = ToStream("");
        var events = await Collect(stream);
        Assert.Empty(events);
    }

    [Fact]
    public async Task UnknownFields_Ignored()
    {
        var stream = ToStream("unknown: value\ndata: test\n\n");
        var events = await Collect(stream);

        Assert.Single(events);
        Assert.Equal("test", events[0].Data);
    }

    [Fact]
    public async Task EventAtEof_WithoutTrailingBlankLine()
    {
        // Event data at EOF without trailing blank line — should still emit
        var stream = ToStream("data: last event");
        var events = await Collect(stream);

        Assert.Single(events);
        Assert.Equal("last event", events[0].Data);
    }

    [Fact]
    public async Task ComplexEvent_AllFields()
    {
        var stream = ToStream("event: update\nid: 99\nretry: 5000\ndata: line1\ndata: line2\n\n");
        var events = await Collect(stream);

        Assert.Single(events);
        Assert.Equal("update", events[0].EventType);
        Assert.Equal("99", events[0].Id);
        Assert.Equal(5000, events[0].Retry);
        Assert.Equal("line1\nline2", events[0].Data);
    }

    [Fact]
    public async Task JsonDataInEvent()
    {
        var json = """{"jsonrpc":"2.0","id":1,"result":{}}""";
        var stream = ToStream($"data: {json}\n\n");
        var events = await Collect(stream);

        Assert.Single(events);
        Assert.Equal(json, events[0].Data);
    }

    private static async Task<List<SseEvent>> Collect(Stream stream)
    {
        var events = new List<SseEvent>();
        await foreach (var evt in SseParser.ParseAsync(stream))
            events.Add(evt);
        return events;
    }
}

public class SseWriterTests
{
    [Fact]
    public async Task WritesSimpleEvent()
    {
        using var stream = new MemoryStream();
        var writer = new SseWriter(stream);

        await writer.WriteEventAsync(new SseEvent { Data = "hello" });

        var output = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("data: hello", output);
        Assert.EndsWith("\n\n", output);
    }

    [Fact]
    public async Task WritesEventType()
    {
        using var stream = new MemoryStream();
        var writer = new SseWriter(stream);

        await writer.WriteEventAsync(new SseEvent { EventType = "custom", Data = "test" });

        var output = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("event: custom", output);
        Assert.Contains("data: test", output);
    }

    [Fact]
    public async Task WritesEventId()
    {
        using var stream = new MemoryStream();
        var writer = new SseWriter(stream);

        await writer.WriteEventAsync(new SseEvent { Data = "test", Id = "42" });

        var output = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("id: 42", output);
    }

    [Fact]
    public async Task WritesMultiLineData()
    {
        using var stream = new MemoryStream();
        var writer = new SseWriter(stream);

        await writer.WriteEventAsync(new SseEvent { Data = "line1\nline2" });

        var output = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("data: line1", output);
        Assert.Contains("data: line2", output);
    }

    [Fact]
    public async Task WritesComment()
    {
        using var stream = new MemoryStream();
        var writer = new SseWriter(stream);

        await writer.WriteCommentAsync("keep-alive");

        var output = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains(": keep-alive", output);
    }

    [Fact]
    public async Task RoundTrip_WriteAndParse()
    {
        using var stream = new MemoryStream();
        var writer = new SseWriter(stream);

        await writer.WriteEventAsync(new SseEvent
        {
            EventType = "update",
            Data = """{"key":"value"}""",
            Id = "1"
        });

        stream.Position = 0;
        var events = new List<SseEvent>();
        await foreach (var evt in SseParser.ParseAsync(stream))
            events.Add(evt);

        Assert.Single(events);
        Assert.Equal("update", events[0].EventType);
        Assert.Equal("""{"key":"value"}""", events[0].Data);
        Assert.Equal("1", events[0].Id);
    }
}
