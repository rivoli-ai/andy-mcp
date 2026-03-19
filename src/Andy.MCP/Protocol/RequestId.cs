using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.MCP.Protocol;

/// <summary>
/// A JSON-RPC request identifier that can be either a string or a number (long).
/// Per the MCP spec, request IDs MUST NOT be null and MUST be unique per session.
/// </summary>
[JsonConverter(typeof(RequestIdJsonConverter))]
public readonly record struct RequestId : IEquatable<RequestId>
{
    private readonly string? _stringValue;
    private readonly long _numberValue;
    private readonly bool _isString;

    private RequestId(string value)
    {
        _stringValue = value ?? throw new ArgumentNullException(nameof(value));
        _numberValue = 0;
        _isString = true;
    }

    private RequestId(long value)
    {
        _stringValue = null;
        _numberValue = value;
        _isString = false;
    }

    public bool IsString => _isString;
    public bool IsNumber => !_isString;

    public string AsString() => _isString
        ? _stringValue!
        : throw new InvalidOperationException("RequestId is a number, not a string.");

    public long AsNumber() => !_isString
        ? _numberValue
        : throw new InvalidOperationException("RequestId is a string, not a number.");

    public static implicit operator RequestId(string value) => new(value);
    public static implicit operator RequestId(long value) => new(value);
    public static implicit operator RequestId(int value) => new((long)value);

    public bool Equals(RequestId other)
    {
        if (_isString != other._isString) return false;
        return _isString
            ? string.Equals(_stringValue, other._stringValue, StringComparison.Ordinal)
            : _numberValue == other._numberValue;
    }

    public override int GetHashCode() => _isString
        ? StringComparer.Ordinal.GetHashCode(_stringValue!)
        : _numberValue.GetHashCode();

    public override string ToString() => _isString ? _stringValue! : _numberValue.ToString();
}

public sealed class RequestIdJsonConverter : JsonConverter<RequestId>
{
    public override RequestId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => (RequestId)reader.GetString()!,
            JsonTokenType.Number => (RequestId)reader.GetInt64(),
            JsonTokenType.Null => throw new JsonException("RequestId must not be null."),
            _ => throw new JsonException($"Unexpected token type {reader.TokenType} for RequestId.")
        };
    }

    public override void Write(Utf8JsonWriter writer, RequestId value, JsonSerializerOptions options)
    {
        if (value.IsString)
            writer.WriteStringValue(value.AsString());
        else
            writer.WriteNumberValue(value.AsNumber());
    }
}
