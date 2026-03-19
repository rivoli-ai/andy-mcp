using System.Text.Json;
using Andy.MCP.Protocol;

namespace Andy.MCP.Tests.Protocol;

public class JsonRpcMessageTests
{
    #region Request Serialization

    [Fact]
    public void Request_WithStringId_RoundTrips()
    {
        var request = new JsonRpcRequest
        {
            Id = "req-1",
            Method = "tools/list",
            Params = McpJsonDefaults.ToElement(new { cursor = "abc" })
        };

        var json = McpJsonDefaults.Serialize(request);
        var deserialized = McpJsonDefaults.Deserialize(json);

        var result = Assert.IsType<JsonRpcRequest>(deserialized);
        Assert.Equal("2.0", result.JsonRpc);
        Assert.Equal("req-1", result.Id.AsString());
        Assert.Equal("tools/list", result.Method);
        Assert.NotNull(result.Params);
    }

    [Fact]
    public void Request_WithNumberId_RoundTrips()
    {
        var request = new JsonRpcRequest
        {
            Id = 42,
            Method = "ping"
        };

        var json = McpJsonDefaults.Serialize(request);
        var deserialized = McpJsonDefaults.Deserialize(json);

        var result = Assert.IsType<JsonRpcRequest>(deserialized);
        Assert.Equal(42L, result.Id.AsNumber());
        Assert.Equal("ping", result.Method);
        Assert.Null(result.Params);
    }

    [Fact]
    public void Request_WithoutParams_OmitsParamsInJson()
    {
        var request = new JsonRpcRequest { Id = 1, Method = "ping" };
        var json = McpJsonDefaults.Serialize(request);

        Assert.DoesNotContain("params", json);
    }

    [Fact]
    public void Request_WithEmptyParams_IncludesEmptyObject()
    {
        var request = new JsonRpcRequest
        {
            Id = 1,
            Method = "test",
            Params = McpJsonDefaults.ToElement(new { })
        };

        var json = McpJsonDefaults.Serialize(request);
        Assert.Contains("\"params\":{}", json);
    }

    #endregion

    #region Response Serialization

    [Fact]
    public void Response_WithResult_RoundTrips()
    {
        var response = JsonRpcResponse.Success(
            id: (RequestId)"resp-1",
            result: McpJsonDefaults.ToElement(new { tools = new[] { "tool1" } }));

        var json = McpJsonDefaults.Serialize(response);
        var deserialized = McpJsonDefaults.Deserialize(json);

        var result = Assert.IsType<JsonRpcResponse>(deserialized);
        Assert.True(result.IsSuccess);
        Assert.False(result.IsError);
        Assert.Equal("resp-1", result.Id.AsString());
        Assert.NotNull(result.Result);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Response_WithError_RoundTrips()
    {
        var response = JsonRpcResponse.Failure(
            id: (RequestId)99,
            error: JsonRpcError.MethodNotFound("No such method: foo/bar"));

        var json = McpJsonDefaults.Serialize(response);
        var deserialized = McpJsonDefaults.Deserialize(json);

        var result = Assert.IsType<JsonRpcResponse>(deserialized);
        Assert.True(result.IsError);
        Assert.False(result.IsSuccess);
        Assert.Equal(99L, result.Id.AsNumber());
        Assert.Equal(McpErrorCodes.MethodNotFound, result.Error!.Code);
        Assert.Equal("No such method: foo/bar", result.Error.Message);
    }

    [Fact]
    public void Response_NeverHasBothResultAndError()
    {
        // Manually craft invalid JSON
        var json = """{"jsonrpc":"2.0","id":1,"result":{},"error":{"code":-32600,"message":"bad"}}""";

        Assert.Throws<JsonRpcParseException>(() => McpJsonDefaults.Deserialize(json));
    }

    [Fact]
    public void Response_WithErrorData_PreservesData()
    {
        var errorData = McpJsonDefaults.ToElement(new { details = "some context", retryable = true });
        var error = new JsonRpcError
        {
            Code = McpErrorCodes.InternalError,
            Message = "Something failed",
            Data = errorData
        };

        var response = JsonRpcResponse.Failure((RequestId)1, error);
        var json = McpJsonDefaults.Serialize(response);
        var deserialized = Assert.IsType<JsonRpcResponse>(McpJsonDefaults.Deserialize(json));

        Assert.NotNull(deserialized.Error!.Data);
        Assert.Equal("some context", deserialized.Error.Data.Value.GetProperty("details").GetString());
        Assert.True(deserialized.Error.Data.Value.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public void Response_GetResult_DeserializesTyped()
    {
        var response = JsonRpcResponse.Success(
            (RequestId)1,
            McpJsonDefaults.ToElement(new { name = "test", count = 5 }));

        var json = McpJsonDefaults.Serialize(response);
        var deserialized = Assert.IsType<JsonRpcResponse>(McpJsonDefaults.Deserialize(json));

        var typed = deserialized.GetResult<TestResult>();
        Assert.NotNull(typed);
        Assert.Equal("test", typed!.Name);
        Assert.Equal(5, typed.Count);
    }

    private record TestResult
    {
        public string Name { get; init; } = "";
        public int Count { get; init; }
    }

    #endregion

    #region Notification Serialization

    [Fact]
    public void Notification_RoundTrips()
    {
        var notification = new JsonRpcNotification
        {
            Method = "notifications/tools/list_changed",
            Params = McpJsonDefaults.ToElement(new { })
        };

        var json = McpJsonDefaults.Serialize(notification);
        var deserialized = McpJsonDefaults.Deserialize(json);

        var result = Assert.IsType<JsonRpcNotification>(deserialized);
        Assert.Equal("notifications/tools/list_changed", result.Method);
    }

    [Fact]
    public void Notification_HasNoIdInJson()
    {
        var notification = new JsonRpcNotification { Method = "test" };
        var json = McpJsonDefaults.Serialize(notification);

        Assert.DoesNotContain("\"id\"", json);
    }

    [Fact]
    public void Notification_WithoutParams_OmitsParams()
    {
        var notification = new JsonRpcNotification { Method = "test" };
        var json = McpJsonDefaults.Serialize(notification);

        Assert.DoesNotContain("params", json);
    }

    #endregion

    #region Polymorphic Deserialization

    [Fact]
    public void Deserialize_IdAndMethod_IsRequest()
    {
        var json = """{"jsonrpc":"2.0","id":"x","method":"ping"}""";
        var message = McpJsonDefaults.Deserialize(json);
        Assert.IsType<JsonRpcRequest>(message);
    }

    [Fact]
    public void Deserialize_IdAndResult_IsResponse()
    {
        var json = """{"jsonrpc":"2.0","id":1,"result":{}}""";
        var message = McpJsonDefaults.Deserialize(json);
        Assert.IsType<JsonRpcResponse>(message);
    }

    [Fact]
    public void Deserialize_IdAndError_IsResponse()
    {
        var json = """{"jsonrpc":"2.0","id":1,"error":{"code":-32601,"message":"not found"}}""";
        var message = McpJsonDefaults.Deserialize(json);
        var response = Assert.IsType<JsonRpcResponse>(message);
        Assert.True(response.IsError);
    }

    [Fact]
    public void Deserialize_MethodOnly_IsNotification()
    {
        var json = """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":"x"}}""";
        var message = McpJsonDefaults.Deserialize(json);
        Assert.IsType<JsonRpcNotification>(message);
    }

    [Fact]
    public void Deserialize_UnknownShape_Throws()
    {
        // Has id but no method, result, or error
        var json = """{"jsonrpc":"2.0","id":1}""";
        Assert.Throws<JsonRpcParseException>(() => McpJsonDefaults.Deserialize(json));
    }

    [Fact]
    public void Deserialize_EmptyObject_Throws()
    {
        Assert.Throws<JsonRpcParseException>(() => McpJsonDefaults.Deserialize("{}"));
    }

    [Fact]
    public void Deserialize_MissingJsonRpc_Throws()
    {
        var json = """{"id":1,"method":"test"}""";
        Assert.Throws<JsonRpcParseException>(() => McpJsonDefaults.Deserialize(json));
    }

    [Fact]
    public void Deserialize_WrongJsonRpcVersion_Throws()
    {
        var json = """{"jsonrpc":"1.0","id":1,"method":"test"}""";
        Assert.Throws<JsonRpcParseException>(() => McpJsonDefaults.Deserialize(json));
    }

    [Fact]
    public void Deserialize_NullId_Throws()
    {
        var json = """{"jsonrpc":"2.0","id":null,"method":"test"}""";
        Assert.Throws<JsonRpcParseException>(() => McpJsonDefaults.Deserialize(json));
    }

    [Fact]
    public void Deserialize_BatchArray_Throws()
    {
        var json = """[{"jsonrpc":"2.0","id":1,"method":"test"}]""";
        Assert.Throws<JsonRpcParseException>(() => McpJsonDefaults.Deserialize(json));
    }

    #endregion

    #region Unicode and Large Payloads

    [Fact]
    public void Request_UnicodeInParams_Preserved()
    {
        var request = new JsonRpcRequest
        {
            Id = 1,
            Method = "test",
            Params = McpJsonDefaults.ToElement(new { text = "日本語テスト 🎉 émojis" })
        };

        var json = McpJsonDefaults.Serialize(request);
        var deserialized = Assert.IsType<JsonRpcRequest>(McpJsonDefaults.Deserialize(json));
        var text = deserialized.Params!.Value.GetProperty("text").GetString();
        Assert.Equal("日本語テスト 🎉 émojis", text);
    }

    [Fact]
    public void Request_LargePayload_SerializesCorrectly()
    {
        var largeText = new string('x', 1_000_000); // 1MB
        var request = new JsonRpcRequest
        {
            Id = 1,
            Method = "test",
            Params = McpJsonDefaults.ToElement(new { data = largeText })
        };

        var json = McpJsonDefaults.Serialize(request);
        var deserialized = Assert.IsType<JsonRpcRequest>(McpJsonDefaults.Deserialize(json));
        var data = deserialized.Params!.Value.GetProperty("data").GetString();
        Assert.Equal(1_000_000, data!.Length);
    }

    #endregion

    #region GetParams Typed Deserialization

    [Fact]
    public void Request_GetParams_DeserializesTyped()
    {
        var request = new JsonRpcRequest
        {
            Id = 1,
            Method = "test",
            Params = McpJsonDefaults.ToElement(new { city = "Paris", unit = "celsius" })
        };

        var json = McpJsonDefaults.Serialize(request);
        var deserialized = Assert.IsType<JsonRpcRequest>(McpJsonDefaults.Deserialize(json));

        var typed = deserialized.GetParams<WeatherParams>();
        Assert.NotNull(typed);
        Assert.Equal("Paris", typed!.City);
        Assert.Equal("celsius", typed.Unit);
    }

    [Fact]
    public void Request_GetParams_NullParams_ReturnsNull()
    {
        var request = new JsonRpcRequest { Id = 1, Method = "test" };
        var json = McpJsonDefaults.Serialize(request);
        var deserialized = Assert.IsType<JsonRpcRequest>(McpJsonDefaults.Deserialize(json));

        Assert.Null(deserialized.GetParams<WeatherParams>());
    }

    private record WeatherParams
    {
        public string City { get; init; } = "";
        public string Unit { get; init; } = "";
    }

    #endregion
}
