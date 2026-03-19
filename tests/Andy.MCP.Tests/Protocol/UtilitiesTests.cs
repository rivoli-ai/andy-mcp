using System.Text.Json;
using Andy.MCP.Protocol;

namespace Andy.MCP.Tests.Protocol;

public class PingTests
{
    [Fact]
    public void Ping_Request_RoundTrips()
    {
        var ping = McpMessages.Ping((RequestId)1);
        var json = McpJsonDefaults.Serialize(ping);
        var deserialized = Assert.IsType<JsonRpcRequest>(McpJsonDefaults.Deserialize(json));

        Assert.Equal(1L, deserialized.Id.AsNumber());
        Assert.Equal("ping", deserialized.Method);
        Assert.Null(deserialized.Params);
    }

    [Fact]
    public void Ping_Response_IsEmptyResult()
    {
        var response = McpMessages.PingResponse((RequestId)1);
        var json = McpJsonDefaults.Serialize(response);
        var deserialized = Assert.IsType<JsonRpcResponse>(McpJsonDefaults.Deserialize(json));

        Assert.True(deserialized.IsSuccess);
        Assert.Equal(1L, deserialized.Id.AsNumber());
    }

    [Fact]
    public void Ping_WithStringId()
    {
        var ping = McpMessages.Ping((RequestId)"ping-42");
        var json = McpJsonDefaults.Serialize(ping);
        var deserialized = Assert.IsType<JsonRpcRequest>(McpJsonDefaults.Deserialize(json));

        Assert.Equal("ping-42", deserialized.Id.AsString());
        Assert.Equal("ping", deserialized.Method);
    }
}

public class CancellationTests
{
    [Fact]
    public void Cancelled_Notification_RoundTrips()
    {
        var notification = McpMessages.Cancelled((RequestId)"req-5", "User requested cancellation");
        var json = McpJsonDefaults.Serialize(notification);
        var deserialized = Assert.IsType<JsonRpcNotification>(McpJsonDefaults.Deserialize(json));

        Assert.Equal("notifications/cancelled", deserialized.Method);

        var p = deserialized.GetParams<CancelledParams>()!;
        Assert.Equal("req-5", p.RequestId.AsString());
        Assert.Equal("User requested cancellation", p.Reason);
    }

    [Fact]
    public void Cancelled_WithoutReason()
    {
        var notification = McpMessages.Cancelled((RequestId)10);
        var json = McpJsonDefaults.Serialize(notification);

        Assert.DoesNotContain("reason", json);

        var deserialized = Assert.IsType<JsonRpcNotification>(McpJsonDefaults.Deserialize(json));
        var p = deserialized.GetParams<CancelledParams>()!;
        Assert.Equal(10L, p.RequestId.AsNumber());
        Assert.Null(p.Reason);
    }

    [Fact]
    public void Cancelled_IsNotification_NoId()
    {
        var notification = McpMessages.Cancelled((RequestId)1);
        var json = McpJsonDefaults.Serialize(notification);

        // Notifications don't have an 'id' field
        Assert.DoesNotContain("\"id\"", json);
    }
}

public class ProgressTests
{
    [Fact]
    public void Progress_WithTotal_RoundTrips()
    {
        var notification = McpMessages.Progress(
            progressToken: (RequestId)"tok-1",
            progress: 5,
            total: 10,
            message: "Processing items");

        var json = McpJsonDefaults.Serialize(notification);
        var deserialized = Assert.IsType<JsonRpcNotification>(McpJsonDefaults.Deserialize(json));

        Assert.Equal("notifications/progress", deserialized.Method);

        var p = deserialized.GetParams<ProgressParams>()!;
        Assert.Equal("tok-1", p.ProgressToken.AsString());
        Assert.Equal(5.0, p.Progress);
        Assert.Equal(10.0, p.Total);
        Assert.Equal("Processing items", p.Message);
    }

    [Fact]
    public void Progress_WithoutTotal_Indeterminate()
    {
        var notification = McpMessages.Progress(
            progressToken: (RequestId)42,
            progress: 3);

        var json = McpJsonDefaults.Serialize(notification);
        Assert.DoesNotContain("total", json);
        Assert.DoesNotContain("message", json);

        var deserialized = Assert.IsType<JsonRpcNotification>(McpJsonDefaults.Deserialize(json));
        var p = deserialized.GetParams<ProgressParams>()!;
        Assert.Equal(42L, p.ProgressToken.AsNumber());
        Assert.Equal(3.0, p.Progress);
        Assert.Null(p.Total);
        Assert.Null(p.Message);
    }

    [Fact]
    public void Progress_FloatingPoint()
    {
        var notification = McpMessages.Progress(
            progressToken: (RequestId)"fp",
            progress: 0.75,
            total: 1.0);

        var json = McpJsonDefaults.Serialize(notification);
        var deserialized = Assert.IsType<JsonRpcNotification>(McpJsonDefaults.Deserialize(json));
        var p = deserialized.GetParams<ProgressParams>()!;

        Assert.Equal(0.75, p.Progress);
        Assert.Equal(1.0, p.Total);
    }

    [Fact]
    public void Progress_IsNotification_NoId()
    {
        var notification = McpMessages.Progress((RequestId)1, 0);
        var json = McpJsonDefaults.Serialize(notification);
        Assert.DoesNotContain("\"id\"", json);
    }
}

public class McpMethodsTests
{
    [Fact]
    public void MethodNames_MatchSpec()
    {
        Assert.Equal("ping", McpMethods.Ping);
        Assert.Equal("notifications/cancelled", McpMethods.NotificationsCancelled);
        Assert.Equal("notifications/progress", McpMethods.NotificationsProgress);
        Assert.Equal("initialize", McpMethods.Initialize);
        Assert.Equal("notifications/initialized", McpMethods.NotificationsInitialized);
        Assert.Equal("tools/list", McpMethods.ToolsList);
        Assert.Equal("tools/call", McpMethods.ToolsCall);
        Assert.Equal("resources/list", McpMethods.ResourcesList);
        Assert.Equal("resources/read", McpMethods.ResourcesRead);
        Assert.Equal("prompts/list", McpMethods.PromptsList);
        Assert.Equal("prompts/get", McpMethods.PromptsGet);
        Assert.Equal("completion/complete", McpMethods.CompletionComplete);
        Assert.Equal("logging/setLevel", McpMethods.LoggingSetLevel);
        Assert.Equal("roots/list", McpMethods.RootsList);
        Assert.Equal("sampling/createMessage", McpMethods.SamplingCreateMessage);
        Assert.Equal("elicitation/create", McpMethods.ElicitationCreate);
    }
}
