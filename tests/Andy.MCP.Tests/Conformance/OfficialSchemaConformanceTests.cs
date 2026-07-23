using System.Text.Json;
using System.Text.Json.Nodes;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Json.Schema;

namespace Andy.MCP.Tests.Conformance;

/// <summary>
/// Validates that Andy.MCP's serialized output conforms to the official MCP 2025-11-25 JSON schema
/// (issue #74). Each fixture is serialized with the library's own options and validated against the
/// corresponding definition in the official schema using a JSON Schema 2020-12 validator. If the
/// output ever violates the schema, these tests (and therefore CI) fail.
/// </summary>
public class OfficialSchemaConformanceTests
{
    private static readonly Uri SchemaUri = new("https://modelcontextprotocol.io/schema/2025-11-25/schema.json");
    private static readonly EvaluationOptions Options = BuildOptions();

    private static EvaluationOptions BuildOptions()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Conformance", "schemas", "schema-2025-11-25.json");
        var schema = JsonSchema.FromText(File.ReadAllText(path));
        var options = new EvaluationOptions { OutputFormat = OutputFormat.List };
        options.SchemaRegistry.Register(SchemaUri, schema);
        return options;
    }

    private static void AssertConforms(string defName, string json)
    {
        var wrapper = new JsonSchemaBuilder().Ref($"{SchemaUri}#/$defs/{defName}").Build();
        var node = JsonNode.Parse(json);
        var result = wrapper.Evaluate(node, Options);

        if (!result.IsValid)
        {
            var errors = string.Join("; ", (result.Details ?? [])
                .Where(d => d.HasErrors)
                .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}")));
            Assert.Fail($"'{defName}' output does not conform to the official schema.\nJSON: {json}\nErrors: {errors}");
        }
    }

    private static bool Conforms(string defName, string json)
    {
        var wrapper = new JsonSchemaBuilder().Ref($"{SchemaUri}#/$defs/{defName}").Build();
        return wrapper.Evaluate(JsonNode.Parse(json), Options).IsValid;
    }

    private static string Ser<T>(T value) => JsonSerializer.Serialize(value, McpJsonDefaults.Options);
    private static JsonElement Obj(object value) => McpJsonDefaults.ToElement(value);

    [Fact]
    public void Validator_RejectsNonConformingOutput()
    {
        // A Tool missing the required "name" must be rejected — proves the gate catches violations.
        Assert.False(Conforms("Tool", """{"inputSchema":{"type":"object"}}"""));
        // A TextContent with the wrong discriminator is not a valid TextContent.
        Assert.False(Conforms("TextContent", """{"type":"image","text":"x"}"""));
    }

    [Fact]
    public void Tool_Conforms() => AssertConforms("Tool", Ser(new Tool
    {
        Name = "add",
        Title = "Add",
        Description = "adds numbers",
        InputSchema = Obj(new { type = "object", properties = new { a = new { type = "integer" } } }),
        OutputSchema = Obj(new { type = "object" }),
        Icons = new[] { new Icon { Source = "https://x/i.png", MimeType = "image/png" } },
        Meta = Obj(new { vendor = "x" })
    }));

    [Fact]
    public void Implementation_Conforms() => AssertConforms("Implementation", Ser(new Implementation("srv", "1.2.3")
    {
        Title = "Server",
        Description = "a server",
        WebsiteUrl = "https://example.com",
        Icons = new[] { new Icon { Source = "https://x/i.png" } }
    }));

    [Fact]
    public void TextContent_Conforms() =>
        AssertConforms("TextContent", Ser<Content>(new TextContent { Text = "hello" }));

    [Fact]
    public void ImageContent_Conforms() =>
        AssertConforms("ImageContent", Ser<Content>(new ImageContent { Data = "AQID", MimeType = "image/png" }));

    [Fact]
    public void AudioContent_Conforms() =>
        AssertConforms("AudioContent", Ser<Content>(new AudioContent { Data = "AQID", MimeType = "audio/wav" }));

    [Fact]
    public void CallToolResult_Conforms() => AssertConforms("CallToolResult", Ser(new CallToolResult
    {
        Content = [new TextContent { Text = "3" }],
        StructuredContent = Obj(new { sum = 3 }),
        IsError = false,
        Meta = Obj(new { trace = "abc" })
    }));

    [Fact]
    public void Resource_Conforms() => AssertConforms("Resource", Ser(new Resource
    {
        Uri = "file:///a",
        Name = "a",
        Title = "A",
        Description = "a file",
        MimeType = "text/plain",
        Size = 10
    }));

    [Fact]
    public void ResourceTemplate_Conforms() => AssertConforms("ResourceTemplate", Ser(new ResourceTemplate
    {
        UriTemplate = "file:///{path}",
        Name = "files"
    }));

    [Fact]
    public void Prompt_Conforms() => AssertConforms("Prompt", Ser(new Prompt
    {
        Name = "greet",
        Description = "greeting",
        Arguments = new[] { new PromptArgument { Name = "who", Required = true } }
    }));

    [Fact]
    public void Icon_Conforms() => AssertConforms("Icon", Ser(new Icon
    {
        Source = "https://x/i.svg",
        MimeType = "image/svg+xml",
        Sizes = new[] { "48x48" },
        Theme = "dark"
    }));

    [Fact]
    public void InitializeResult_Conforms() => AssertConforms("InitializeResult", Ser(new InitializeResult
    {
        ProtocolVersion = "2025-11-25",
        Capabilities = new ServerCapabilities { Tools = new ListChangedCapability { ListChanged = true } },
        ServerInfo = new Implementation("srv", "1.0"),
        Instructions = "use me"
    }));

    [Fact]
    public void ReadResourceResult_Conforms() => AssertConforms("ReadResourceResult", Ser(new ReadResourceResult
    {
        Contents = [new TextResourceContents { Uri = "file:///a", Text = "hi" }]
    }));

    [Fact]
    public void CreateMessageResult_Conforms() => AssertConforms("CreateMessageResult", Ser(new CreateMessageResult
    {
        Role = Role.Assistant,
        Content = [new TextContent { Text = "hi" }],
        Model = "m",
        StopReason = "endTurn"
    }));
}
