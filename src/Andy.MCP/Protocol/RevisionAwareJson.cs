using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Andy.MCP.Protocol;

/// <summary>
/// Marks a model property as introduced in a specific protocol revision. Revision-aware
/// serialization drops such properties when serializing for an older negotiated revision, so a
/// peer never receives fields its revision does not define.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class SinceRevisionAttribute : Attribute
{
    /// <summary>The dated protocol version string in which the property was introduced.</summary>
    public string Version { get; }

    public SinceRevisionAttribute(string version) => Version = version;
}

/// <summary>
/// Revision-aware JSON serialization. Produces output whose fields are valid for a specific
/// negotiated <see cref="ProtocolRevision"/> by dropping any property annotated with a
/// <see cref="SinceRevisionAttribute"/> newer than the target revision.
///
/// This is type-aware (it runs per model type via a contract modifier), so it never confuses a
/// newer field with an equally-named older field on a different type. Serialize the typed payload
/// objects (results, params) through <see cref="ToElementForRevision{T}"/> at the point they are
/// built — while the negotiated revision is known — rather than pruning opaque JsonElement trees.
/// </summary>
public static class RevisionAwareJson
{
    private static readonly ConcurrentDictionary<int, JsonSerializerOptions> _optionsByOrdinal = new();

    /// <summary>
    /// Serializer options that emit only fields valid for <paramref name="revision"/>. Cached per
    /// revision. When the revision is the latest, no properties are dropped.
    /// </summary>
    public static JsonSerializerOptions OptionsFor(ProtocolRevision revision) =>
        _optionsByOrdinal.GetOrAdd(revision.Ordinal, static ordinal =>
        {
            var options = new JsonSerializerOptions(McpJsonDefaults.Options)
            {
                TypeInfoResolver = new DefaultJsonTypeInfoResolver
                {
                    Modifiers = { typeInfo => DropNewerProperties(typeInfo, ordinal) }
                }
            };
            return options;
        });

    /// <summary>Serialize a value to a JsonElement, omitting fields newer than the revision.</summary>
    public static JsonElement ToElementForRevision<T>(T value, ProtocolRevision revision) =>
        JsonSerializer.SerializeToElement(value, OptionsFor(revision));

    /// <summary>Serialize a value to a string, omitting fields newer than the revision.</summary>
    public static string SerializeForRevision<T>(T value, ProtocolRevision revision) =>
        JsonSerializer.Serialize(value, OptionsFor(revision));

    private static void DropNewerProperties(JsonTypeInfo typeInfo, int targetOrdinal)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
            return;

        for (int i = typeInfo.Properties.Count - 1; i >= 0; i--)
        {
            var attribute = (typeInfo.Properties[i].AttributeProvider as MemberInfo)
                ?.GetCustomAttribute<SinceRevisionAttribute>();
            if (attribute is null)
                continue;

            var introducedIn = ProtocolRevision.TryGet(attribute.Version);
            if (introducedIn is not null && introducedIn.Ordinal > targetOrdinal)
                typeInfo.Properties.RemoveAt(i);
        }
    }
}
