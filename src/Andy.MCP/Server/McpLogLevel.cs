using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.MCP.Server;

/// <summary>
/// MCP log levels matching RFC 5424 syslog severity.
/// Ordered from most verbose (Debug=7) to least verbose (Emergency=0).
/// </summary>
[JsonConverter(typeof(McpLogLevelConverter))]
public enum McpLogLevel
{
    Emergency = 0,
    Alert = 1,
    Critical = 2,
    Error = 3,
    Warning = 4,
    Notice = 5,
    Info = 6,
    Debug = 7
}

public sealed class McpLogLevelConverter : JsonConverter<McpLogLevel>
{
    public override McpLogLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value switch
        {
            "emergency" => McpLogLevel.Emergency,
            "alert" => McpLogLevel.Alert,
            "critical" => McpLogLevel.Critical,
            "error" => McpLogLevel.Error,
            "warning" => McpLogLevel.Warning,
            "notice" => McpLogLevel.Notice,
            "info" => McpLogLevel.Info,
            "debug" => McpLogLevel.Debug,
            _ => throw new JsonException($"Unknown MCP log level: '{value}'")
        };
    }

    public override void Write(Utf8JsonWriter writer, McpLogLevel value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            McpLogLevel.Emergency => "emergency",
            McpLogLevel.Alert => "alert",
            McpLogLevel.Critical => "critical",
            McpLogLevel.Error => "error",
            McpLogLevel.Warning => "warning",
            McpLogLevel.Notice => "notice",
            McpLogLevel.Info => "info",
            McpLogLevel.Debug => "debug",
            _ => throw new JsonException($"Unknown McpLogLevel: {value}")
        });
    }
}

/// <summary>
/// Params for logging/setLevel request.
/// </summary>
public sealed record SetLogLevelParams
{
    [JsonPropertyName("level")]
    public required McpLogLevel Level { get; init; }
}

/// <summary>
/// Params for notifications/message log notification.
/// </summary>
public sealed record LogMessageParams
{
    [JsonPropertyName("level")]
    public required McpLogLevel Level { get; init; }

    [JsonPropertyName("logger")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Logger { get; init; }

    [JsonPropertyName("data")]
    public required JsonElement Data { get; init; }
}
