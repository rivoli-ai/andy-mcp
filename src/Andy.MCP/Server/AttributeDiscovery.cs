using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.ComponentModel;
using System.Text.RegularExpressions;
using Andy.MCP.Protocol;

namespace Andy.MCP.Server;

/// <summary>
/// Discovers [McpTool], [McpResource], and [McpPrompt] attributed methods
/// and registers them on an McpServer instance.
/// </summary>
public static class AttributeDiscovery
{
    /// <summary>
    /// Discover and register all attributed methods from a type.
    /// </summary>
    [RequiresUnreferencedCode("Attribute discovery reflects over methods and serialized parameter types; preserve all registered types.")]
    [RequiresDynamicCode("Attribute schemas and argument binding use runtime-generated serializer metadata.")]
    public static McpServer AddToolsFromType(this McpServer server, Type type, object? instance = null)
    {
        // Create at most one instance for the whole type, lazily and reused across every attributed
        // method — never a fresh instance per method.
        var shared = instance;
        object? InstanceFor(MethodInfo m) => m.IsStatic ? null : shared ??= CreateInstance(type);

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            var toolAttr = method.GetCustomAttribute<McpToolAttribute>();
            if (toolAttr is not null)
            {
                RegisterTool(server, method, toolAttr, InstanceFor(method));
                continue;
            }

            var resourceAttr = method.GetCustomAttribute<McpResourceAttribute>();
            if (resourceAttr is not null)
            {
                RegisterResource(server, method, resourceAttr, InstanceFor(method));
                continue;
            }

            var promptAttr = method.GetCustomAttribute<McpPromptAttribute>();
            if (promptAttr is not null)
            {
                RegisterPrompt(server, method, promptAttr, InstanceFor(method));
            }
        }

        return server;
    }

    /// <summary>
    /// Discover and register all attributed methods from a type (generic).
    /// </summary>
    [RequiresUnreferencedCode("Attribute discovery reflects over methods and serialized parameter types; preserve all registered types.")]
    [RequiresDynamicCode("Attribute schemas and argument binding use runtime-generated serializer metadata.")]
    public static McpServer AddToolsFromType<T>(this McpServer server) =>
        server.AddToolsFromType(typeof(T));

    /// <summary>
    /// Discover and register all attributed methods from all types in an assembly.
    /// </summary>
    [RequiresUnreferencedCode("Attribute discovery reflects over methods and serialized parameter types; preserve all registered types.")]
    [RequiresDynamicCode("Attribute schemas and argument binding use runtime-generated serializer metadata.")]
    public static McpServer AddToolsFromAssembly(this McpServer server, Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            var hasMcpMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Any(m => m.GetCustomAttribute<McpToolAttribute>() is not null
                    || m.GetCustomAttribute<McpResourceAttribute>() is not null
                    || m.GetCustomAttribute<McpPromptAttribute>() is not null);

            if (hasMcpMethods)
                server.AddToolsFromType(type);
        }

        return server;
    }

    #region Tool Registration

    private static void RegisterTool(McpServer server, MethodInfo method, McpToolAttribute attr, object? instance)
    {
        var name = attr.Name ?? ToSnakeCase(method.Name);
        var description = attr.Description ?? "";
        var schema = GenerateInputSchema(method);
        var annotations = new ToolAnnotations
        {
            Title = attr.Title,
            ReadOnlyHint = attr.ReadOnly ? true : null,
            DestructiveHint = attr.Destructive ? null : false, // Default is true, only set if explicitly false
            IdempotentHint = attr.Idempotent ? true : null,
            OpenWorldHint = attr.OpenWorld ? null : false
        };

        var tool = attr.DefinitionJson is { } definition
            ? JsonSerializer.Deserialize<Tool>(definition, McpJsonDefaults.Options)!
            : new Tool
            {
                Name = name,
                Title = attr.Title,
                Description = description,
                Annotations = annotations,
                InputSchema = attr.InputSchemaJson is { } input ? JsonSerializer.Deserialize<JsonElement>(input) : schema,
                OutputSchema = attr.OutputSchemaJson is { } output ? JsonSerializer.Deserialize<JsonElement>(output) : null,
                Icons = attr.IconsJson is { } icons ? JsonSerializer.Deserialize<List<Icon>>(icons, McpJsonDefaults.Options) : null,
                Meta = attr.MetaJson is { } meta ? JsonSerializer.Deserialize<JsonElement>(meta) : null,
                Execution = new ToolExecution { TaskSupport = attr.TaskSupport }
            };
        server.AddTool(tool, async (args, progress, ct) =>
        {
            var parameters = BindParameters(method, args, progress, ct);
            object? result;
            try { result = method.Invoke(method.IsStatic ? null : instance, parameters); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
            return await CoerceToolResult(result, method.ReturnType, tool.OutputSchema is not null);
        });
    }

    private static readonly JsonSerializerOptions ArgumentOptions = CreateArgumentOptions();

    private static JsonSerializerOptions CreateArgumentOptions()
    {
        var options = new JsonSerializerOptions(McpJsonDefaults.Options)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly();
        return options;
    }

    private static JsonElement GenerateInputSchema(MethodInfo method)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        var nullability = new NullabilityInfoContext();
        foreach (var parameter in method.GetParameters())
        {
            if (IsInjectedParameter(parameter)) continue;
            var metadata = parameter.GetCustomAttribute<McpParamAttribute>();
            var name = parameter.Name!;
            var nullable = nullability.Create(parameter).ReadState == NullabilityState.Nullable;
            var node = ArgumentOptions.GetJsonSchemaAsNode(parameter.ParameterType, new JsonSchemaExporterOptions
            {
                TreatNullObliviousAsNonNullable = true,
                TransformSchemaNode = (context, schema) =>
                {
                    if (schema is JsonObject obj && context.PropertyInfo?.AttributeProvider is { } provider)
                    {
                        var description = provider.GetCustomAttributes(typeof(DescriptionAttribute), true)
                            .OfType<DescriptionAttribute>().FirstOrDefault();
                        if (description is not null) obj["description"] = description.Description;
                    }
                    return schema;
                }
            });
            RebaseReferences(node, "#/properties/" + name.Replace("~", "~0").Replace("/", "~1"));
            if (nullable)
                node = new JsonObject { ["anyOf"] = new JsonArray(node, new JsonObject { ["type"] = "null" }) };
            if (node is JsonObject property)
            {
                if (metadata?.Description is { } description) property["description"] = description;
                if (parameter.HasDefaultValue)
                    property["default"] = JsonSerializer.SerializeToNode(parameter.DefaultValue, parameter.ParameterType, ArgumentOptions);
            }
            properties[name] = node;
            if (metadata?.Required == true || (!parameter.HasDefaultValue && !nullable))
                required.Add(name);
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0) schema["required"] = required;
        return JsonSerializer.SerializeToElement(schema);
    }

    private static void RebaseReferences(JsonNode? node, string prefix)
    {
        if (node is JsonObject obj)
        {
            if (obj["$ref"] is JsonValue value && value.TryGetValue<string>(out var reference) && reference.StartsWith('#'))
                obj["$ref"] = prefix + reference[1..];
            foreach (var child in obj.ToArray()) RebaseReferences(child.Value, prefix);
        }
        else if (node is JsonArray array)
            foreach (var child in array) RebaseReferences(child, prefix);
    }

    private static object?[] BindParameters(
        MethodInfo method, JsonElement? args, IProgress<McpProgress> progress, CancellationToken ct)
    {
        var methodParams = method.GetParameters();
        var values = new object?[methodParams.Length];

        for (int i = 0; i < methodParams.Length; i++)
        {
            var param = methodParams[i];

            if (param.ParameterType == typeof(CancellationToken))
            {
                values[i] = ct;
                continue;
            }

            if (param.ParameterType == typeof(IProgress<McpProgress>))
            {
                values[i] = progress; // functional reporter injected end-to-end
                continue;
            }

            if (args is not null && args.Value.TryGetProperty(param.Name!, out var value))
            {
                values[i] = JsonSerializer.Deserialize(value, param.ParameterType, ArgumentOptions);
            }
            else if (param.HasDefaultValue)
            {
                values[i] = param.DefaultValue;
            }
            else
            {
                values[i] = param.ParameterType.IsValueType
                    ? Activator.CreateInstance(param.ParameterType)
                    : null;
            }
        }

        return values;
    }

    private static async Task<CallToolResult> CoerceToolResult(object? result, Type returnType, bool structured)
    {
        if (result is ValueTask valueTask)
        {
            await valueTask;
            return CallToolResult.Text("");
        }
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            result = returnType.GetMethod("AsTask")!.Invoke(result, null);
            returnType = typeof(Task<>).MakeGenericType(returnType.GetGenericArguments());
        }
        // Unwrap Task<T>
        if (result is Task task)
        {
            await task;
            if (returnType.IsGenericType)
            {
                var resultProperty = returnType.GetProperty("Result");
                result = resultProperty?.GetValue(task);
            }
            else
            {
                return CallToolResult.Text(""); // Task (void)
            }
        }

        if (structured && result is not CallToolResult)
        {
            var value = JsonSerializer.SerializeToElement(result, ArgumentOptions);
            return new CallToolResult { StructuredContent = value, Content = [new TextContent(value.GetRawText())] };
        }
        return result switch
        {
            CallToolResult ctr => ctr,
            string s => CallToolResult.Text(s),
            null => CallToolResult.Text(""),
            _ => CallToolResult.Text(JsonSerializer.Serialize(result, McpJsonDefaults.Options))
        };
    }

    #endregion

    #region Resource Registration

    private static void RegisterResource(McpServer server, MethodInfo method, McpResourceAttribute attr, object? instance)
    {
        server.AddResource(attr.Uri, attr.Name, async (uri, ct) =>
        {
            var parameters = BindResourceParameters(method, uri, ct);
            var result = method.Invoke(method.IsStatic ? null : instance, parameters);

            if (result is Task<ResourceContents> rcTask) return await rcTask;
            if (result is ResourceContents rc) return rc;

            throw new InvalidOperationException(
                $"[McpResource] method '{method.Name}' must return Task<ResourceContents> or ResourceContents.");
        }, attr.Description, attr.MimeType);
    }

    private static object?[] BindResourceParameters(MethodInfo method, string uri, CancellationToken ct)
    {
        var methodParams = method.GetParameters();
        var values = new object?[methodParams.Length];

        for (int i = 0; i < methodParams.Length; i++)
        {
            if (methodParams[i].ParameterType == typeof(string))
                values[i] = uri;
            else if (methodParams[i].ParameterType == typeof(CancellationToken))
                values[i] = ct;
        }

        return values;
    }

    #endregion

    #region Prompt Registration

    private static void RegisterPrompt(McpServer server, MethodInfo method, McpPromptAttribute attr, object? instance)
    {
        var name = attr.Name ?? ToSnakeCase(method.Name);
        var description = attr.Description ?? "";

        // Build PromptArgument list from method parameters (excluding injected)
        var arguments = method.GetParameters()
            .Where(p => !IsInjectedParameter(p) && p.ParameterType == typeof(string))
            .Select(p =>
            {
                var pa = p.GetCustomAttribute<McpParamAttribute>();
                return new PromptArgument
                {
                    Name = p.Name!,
                    Description = pa?.Description,
                    Required = pa?.Required == true || (!p.HasDefaultValue && !IsNullableType(p.ParameterType))
                        ? true : null
                };
            })
            .ToList();

        server.AddPrompt(name, description, async (promptName, args, ct) =>
        {
            var parameters = BindPromptParameters(method, promptName, args, ct);
            var result = method.Invoke(method.IsStatic ? null : instance, parameters);

            if (result is Task<GetPromptResult> prTask) return await prTask;
            if (result is GetPromptResult pr) return pr;

            throw new InvalidOperationException(
                $"[McpPrompt] method '{method.Name}' must return Task<GetPromptResult> or GetPromptResult.");
        }, arguments.Count > 0 ? arguments : null);
    }

    private static object?[] BindPromptParameters(MethodInfo method, string name,
        IDictionary<string, string>? args, CancellationToken ct)
    {
        var methodParams = method.GetParameters();
        var values = new object?[methodParams.Length];

        for (int i = 0; i < methodParams.Length; i++)
        {
            var param = methodParams[i];
            if (param.ParameterType == typeof(CancellationToken))
            {
                values[i] = ct;
            }
            else if (param.ParameterType == typeof(string) && args is not null && args.TryGetValue(param.Name!, out var v))
            {
                values[i] = v;
            }
            else if (param.HasDefaultValue)
            {
                values[i] = param.DefaultValue;
            }
        }

        return values;
    }

    #endregion

    #region Helpers

    private static bool IsInjectedParameter(ParameterInfo param) =>
        param.ParameterType == typeof(CancellationToken)
        || param.ParameterType == typeof(IProgress<McpProgress>);

    private static bool IsNullableType(Type type) =>
        !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

    /// <summary>
    /// Convert PascalCase to snake_case: GetWeather → get_weather
    /// </summary>
    public static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return Regex.Replace(name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant();
    }

    private static object CreateInstance(Type type)
    {
        try { return Activator.CreateInstance(type)!; }
        catch { throw new InvalidOperationException($"Cannot create instance of '{type.Name}'. Make it have a parameterless constructor or use static methods."); }
    }

    #endregion
}
