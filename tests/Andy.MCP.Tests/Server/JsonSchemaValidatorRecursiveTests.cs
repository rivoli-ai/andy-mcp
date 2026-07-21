using System.Text.Json;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Server;

/// <summary>
/// Tests for the recursive JSON Schema validation added in #47: nested objects, arrays/items,
/// enum/const, string and numeric constraints, and combinators.
/// </summary>
public class JsonSchemaValidatorRecursiveTests
{
    private static JsonElement Schema(object o) => McpJsonDefaults.ToElement(o);
    private static JsonElement? Args(object o) => McpJsonDefaults.ToElement(o);

    [Fact]
    public void NestedObject_InvalidChild_IsReported()
    {
        var schema = Schema(new
        {
            type = "object",
            properties = new
            {
                address = new
                {
                    type = "object",
                    properties = new { zip = new { type = "integer" } },
                    required = new[] { "zip" }
                }
            }
        });

        var missing = JsonSchemaValidator.Validate(Args(new { address = new { } }), schema);
        Assert.Contains(missing, e => e.Contains("zip"));

        var wrongType = JsonSchemaValidator.Validate(Args(new { address = new { zip = "x" } }), schema);
        Assert.Contains(wrongType, e => e.Contains("wrong type"));
    }

    [Fact]
    public void Array_Items_And_Bounds_AreValidated()
    {
        var schema = Schema(new
        {
            type = "object",
            properties = new
            {
                tags = new
                {
                    type = "array",
                    items = new { type = "string" },
                    minItems = 1,
                    maxItems = 2
                }
            }
        });

        Assert.Empty(JsonSchemaValidator.Validate(Args(new { tags = new[] { "a" } }), schema));
        Assert.NotEmpty(JsonSchemaValidator.Validate(Args(new { tags = Array.Empty<string>() }), schema)); // minItems
        Assert.NotEmpty(JsonSchemaValidator.Validate(Args(new { tags = new[] { "a", "b", "c" } }), schema)); // maxItems

        var wrongItem = JsonSchemaValidator.Validate(
            Args(new { tags = new object[] { "a", 3 } }), schema);
        Assert.Contains(wrongItem, e => e.Contains("wrong type"));
    }

    [Fact]
    public void Enum_RejectsDisallowedValue()
    {
        var schema = Schema(new
        {
            type = "object",
            properties = new { color = new { type = "string", @enum = new[] { "red", "green" } } }
        });

        Assert.Empty(JsonSchemaValidator.Validate(Args(new { color = "red" }), schema));
        Assert.NotEmpty(JsonSchemaValidator.Validate(Args(new { color = "blue" }), schema));
    }

    [Fact]
    public void String_Length_And_Pattern_AreValidated()
    {
        var schema = Schema(new
        {
            type = "object",
            properties = new
            {
                code = new { type = "string", minLength = 2, maxLength = 4, pattern = "^[A-Z]+$" }
            }
        });

        Assert.Empty(JsonSchemaValidator.Validate(Args(new { code = "AB" }), schema));
        Assert.NotEmpty(JsonSchemaValidator.Validate(Args(new { code = "A" }), schema));      // too short
        Assert.NotEmpty(JsonSchemaValidator.Validate(Args(new { code = "ABCDE" }), schema));  // too long
        Assert.NotEmpty(JsonSchemaValidator.Validate(Args(new { code = "ab" }), schema));     // pattern
    }

    [Fact]
    public void Numeric_Bounds_And_MultipleOf_AreValidated()
    {
        var schema = Schema(new
        {
            type = "object",
            properties = new
            {
                n = new { type = "number", minimum = 0, maximum = 10, multipleOf = 2 }
            }
        });

        Assert.Empty(JsonSchemaValidator.Validate(Args(new { n = 4 }), schema));
        Assert.NotEmpty(JsonSchemaValidator.Validate(Args(new { n = -1 }), schema)); // below min
        Assert.NotEmpty(JsonSchemaValidator.Validate(Args(new { n = 11 }), schema)); // above max
        Assert.NotEmpty(JsonSchemaValidator.Validate(Args(new { n = 3 }), schema));  // not multiple of 2
    }

    [Fact]
    public void AdditionalProperties_False_RejectsUnknown()
    {
        var schema = Schema(new
        {
            type = "object",
            properties = new { known = new { type = "string" } },
            additionalProperties = false
        });

        Assert.Empty(JsonSchemaValidator.Validate(Args(new { known = "ok" }), schema));
        Assert.NotEmpty(JsonSchemaValidator.Validate(Args(new { known = "ok", extra = 1 }), schema));
    }

    [Fact]
    public void Combinators_AnyOf_OneOf_Not()
    {
        var anyOf = Schema(new { anyOf = new object[] { new { type = "string" }, new { type = "integer" } } });
        Assert.Empty(JsonSchemaValidator.Validate(McpJsonDefaults.ToElement("hi"), anyOf));
        Assert.Empty(JsonSchemaValidator.Validate(McpJsonDefaults.ToElement(5), anyOf));
        Assert.NotEmpty(JsonSchemaValidator.Validate(McpJsonDefaults.ToElement(true), anyOf));

        var oneOf = Schema(new { oneOf = new object[] { new { type = "number", multipleOf = 2 }, new { type = "number", multipleOf = 3 } } });
        Assert.Empty(JsonSchemaValidator.Validate(McpJsonDefaults.ToElement(4), oneOf));   // only 2
        Assert.NotEmpty(JsonSchemaValidator.Validate(McpJsonDefaults.ToElement(6), oneOf)); // both 2 and 3 → not exactly one

        var not = Schema(new { not = new { type = "string" } });
        Assert.Empty(JsonSchemaValidator.Validate(McpJsonDefaults.ToElement(1), not));
        Assert.NotEmpty(JsonSchemaValidator.Validate(McpJsonDefaults.ToElement("x"), not));
    }
}
