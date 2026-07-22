using System.Reflection;
using System.Text.Json;
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
    public static McpServer AddToolsFromType<T>(this McpServer server) =>
        server.AddToolsFromType(typeof(T));

    /// <summary>
    /// Discover and register all attributed methods from all types in an assembly.
    /// </summary>
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
            IdempotentHint = attr.Idempotent ? true : null
        };

        server.AddTool(name, description, schema, async (args, progress, ct) =>
        {
            var parameters = BindParameters(method, args, progress, ct);
            var result = method.Invoke(method.IsStatic ? null : instance, parameters);
            return await CoerceToolResult(result, method.ReturnType);
        }, annotations);
    }

    private static JsonElement GenerateInputSchema(MethodInfo method)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var param in method.GetParameters())
        {
            if (IsInjectedParameter(param)) continue;

            var paramAttr = param.GetCustomAttribute<McpParamAttribute>();
            var schemaType = GetJsonSchemaType(param.ParameterType);
            var prop = new Dictionary<string, object> { ["type"] = schemaType };

            var desc = paramAttr?.Description;
            if (desc is not null) prop["description"] = desc;

            // Array/collection element schema.
            if (schemaType == "array" && ElementTypeOf(param.ParameterType) is { } elementType)
                prop["items"] = new Dictionary<string, object> { ["type"] = GetJsonSchemaType(elementType) };

            // Enum values (also for a nullable enum).
            var enumType = Nullable.GetUnderlyingType(param.ParameterType) ?? param.ParameterType;
            if (enumType.IsEnum)
                prop["enum"] = Enum.GetNames(enumType);

            if (param.HasDefaultValue && param.DefaultValue is not null)
                prop["default"] = param.DefaultValue;

            properties[param.Name!] = prop;

            // Determine if required
            if (paramAttr?.Required == true)
            {
                required.Add(param.Name!);
            }
            else if (!param.HasDefaultValue && !IsNullableType(param.ParameterType))
            {
                required.Add(param.Name!);
            }
        }

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties
        };
        if (required.Count > 0) schema["required"] = required;

        return McpJsonDefaults.ToElement(schema);
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
                values[i] = JsonSerializer.Deserialize(value, param.ParameterType, McpJsonDefaults.Options);
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

    private static async Task<CallToolResult> CoerceToolResult(object? result, Type returnType)
    {
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

    private static string GetJsonSchemaType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(string)) return "string";
        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)) return "integer";
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return "number";
        if (type == typeof(bool)) return "boolean";
        if (type.IsArray || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))) return "array";
        if (type.IsEnum) return "string";

        return "object";
    }

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

    private static Type? ElementTypeOf(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();
        if (type.IsGenericType &&
            (type.GetGenericTypeDefinition() == typeof(List<>) ||
             type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) ||
             type.GetGenericTypeDefinition() == typeof(IList<>) ||
             type.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
            return type.GetGenericArguments()[0];
        return null;
    }

    #endregion
}
