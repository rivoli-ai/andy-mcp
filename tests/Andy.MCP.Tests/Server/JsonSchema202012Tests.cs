using System.Text.Json;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Server;

public class JsonSchema202012Tests
{
    [Theory]
    [InlineData("{\"$defs\":{\"n\":{\"type\":\"integer\"}},\"$ref\":\"#/$defs/n\"}", "3", "\"3\"")]
    [InlineData("{\"prefixItems\":[{\"type\":\"string\"},{\"type\":\"integer\"}],\"items\":false}", "[\"a\",2]", "[\"a\",2,3]")]
    [InlineData("{\"contains\":{\"type\":\"integer\"},\"minContains\":2,\"maxContains\":2}", "[1,2,\"a\"]", "[1,\"a\"]")]
    [InlineData("{\"dependentRequired\":{\"a\":[\"b\"]}}", "{\"a\":1,\"b\":2}", "{\"a\":1}")]
    [InlineData("{\"if\":{\"required\":[\"a\"]},\"then\":{\"required\":[\"b\"]},\"else\":{\"required\":[\"c\"]}}", "{\"c\":1}", "{}")]
    [InlineData("{\"allOf\":[{\"properties\":{\"a\":true}}],\"unevaluatedProperties\":false}", "{\"a\":1}", "{\"b\":1}")]
    [InlineData("{\"type\":\"object\"}", "{}", "null")]
    public void DraftKeywords_AcceptAndReject(string schema, string valid, string invalid)
    {
        var s = JsonDocument.Parse(schema).RootElement;
        Assert.Empty(JsonSchemaValidator.ValidateSchema(s));
        Assert.Empty(JsonSchemaValidator.Validate(JsonDocument.Parse(valid).RootElement, s));
        Assert.NotEmpty(JsonSchemaValidator.Validate(JsonDocument.Parse(invalid).RootElement, s));
    }

    [Theory]
    [InlineData("{\"minItems\":-1}")]
    [InlineData("{\"required\":[\"a\",\"a\"]}")]
    [InlineData("{\"prefixItems\":{}}")]
    public void InvalidKeywordShapes_AreRejected(string schema) =>
        Assert.NotEmpty(JsonSchemaValidator.ValidateSchema(JsonDocument.Parse(schema).RootElement));

    [Fact]
    public void ExternalReference_FailsClosed()
    {
        var schema = JsonDocument.Parse("{\"$ref\":\"https://127.0.0.1/private\"}").RootElement;
        var errors = JsonSchemaValidator.Validate(JsonDocument.Parse("{}").RootElement, schema);
        Assert.Contains(errors, e => e.Contains("External schema reference"));
    }
}
