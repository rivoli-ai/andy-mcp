namespace Andy.MCP.Protocol;

/// <summary>
/// An MCP protocol revision (identified by its dated version string, e.g. "2025-11-25")
/// together with the wire-schema features that revision introduced.
///
/// This is the single source of truth for which revisions this implementation supports and
/// what each revision permits on the wire. Serialization, capability advertisement, and
/// behavior that depends on the negotiated revision should consult the corresponding
/// <see cref="ProtocolRevision"/> (obtained via <see cref="McpSession.Revision"/>) rather
/// than comparing version strings directly, so cross-revision fields are never emitted under
/// an older negotiated revision.
///
/// Feature availability is grounded in the official MCP changelogs
/// (https://modelcontextprotocol.io/specification/2025-11-25/changelog and predecessors).
/// </summary>
public sealed record ProtocolRevision
{
    private ProtocolRevision(string version, int ordinal)
    {
        Version = version;
        Ordinal = ordinal;
    }

    /// <summary>The dated protocol version string as it appears on the wire.</summary>
    public string Version { get; }

    /// <summary>
    /// Monotonic ordering key: a higher ordinal is a newer revision. Used to select the
    /// highest mutually supported revision and to gate revision-specific features.
    /// </summary>
    public int Ordinal { get; }

    // Known revisions, oldest to newest. Ordinals must stay strictly increasing.
    public static readonly ProtocolRevision V2024_11_05 = new("2024-11-05", 0);
    public static readonly ProtocolRevision V2025_03_26 = new("2025-03-26", 1);
    public static readonly ProtocolRevision V2025_06_18 = new("2025-06-18", 2);
    public static readonly ProtocolRevision V2025_11_25 = new("2025-11-25", 3);

    /// <summary>The newest revision this implementation supports.</summary>
    public static ProtocolRevision Latest => V2025_11_25;

    /// <summary>All supported revisions, newest first.</summary>
    public static IReadOnlyList<ProtocolRevision> All { get; } = new[]
    {
        V2025_11_25,
        V2025_06_18,
        V2025_03_26,
        V2024_11_05,
    };

    /// <summary>All supported revision version strings, newest first.</summary>
    public static IReadOnlyList<string> AllVersions { get; } =
        All.Select(r => r.Version).ToArray();

    /// <summary>
    /// Resolve a version string to its <see cref="ProtocolRevision"/>, or null if unsupported.
    /// </summary>
    public static ProtocolRevision? TryGet(string? version) =>
        version is null ? null : All.FirstOrDefault(r => r.Version == version);

    /// <summary>True if this revision is the same as or newer than <paramref name="other"/>.</summary>
    public bool AtLeast(ProtocolRevision other) => Ordinal >= other.Ordinal;

    // --- Feature availability, keyed to the revision that introduced each feature. ---

    // Introduced in 2025-03-26:
    /// <summary>Audio content blocks are permitted.</summary>
    public bool SupportsAudioContent => AtLeast(V2025_03_26);

    /// <summary>Tool annotations (read-only/destructive/idempotent hints) are permitted.</summary>
    public bool SupportsToolAnnotations => AtLeast(V2025_03_26);

    // Introduced in 2025-06-18:
    /// <summary>Elicitation (server-requested user input) is permitted.</summary>
    public bool SupportsElicitation => AtLeast(V2025_06_18);

    /// <summary>Structured tool output (structuredContent / outputSchema) is permitted.</summary>
    public bool SupportsStructuredToolOutput => AtLeast(V2025_06_18);

    /// <summary>Resource links in tool results are permitted.</summary>
    public bool SupportsResourceLinks => AtLeast(V2025_06_18);

    // Introduced in 2025-11-25 (see the 2025-11-25 changelog):
    /// <summary>Icons as metadata on tools, resources, resource templates, and prompts (SEP-973).</summary>
    public bool SupportsIcons => AtLeast(V2025_11_25);

    /// <summary>URL-mode elicitation requests (SEP-1036).</summary>
    public bool SupportsElicitationUrlMode => AtLeast(V2025_11_25);

    /// <summary>Standards-based enum schemas for elicitation, incl. single/multi-select (SEP-1330).</summary>
    public bool SupportsElicitationEnumSchema => AtLeast(V2025_11_25);

    /// <summary>Default values on primitive elicitation schema types (SEP-1034).</summary>
    public bool SupportsSchemaDefaultValues => AtLeast(V2025_11_25);

    /// <summary>Tool calling within sampling via <c>tools</c> and <c>toolChoice</c> (SEP-1577).</summary>
    public bool SupportsSamplingToolCalling => AtLeast(V2025_11_25);

    /// <summary>Experimental durable tasks with polling and deferred results (SEP-1686).</summary>
    public bool SupportsExperimentalTasks => AtLeast(V2025_11_25);

    /// <summary>Optional <c>description</c> field on the Implementation object.</summary>
    public bool SupportsImplementationDescription => AtLeast(V2025_11_25);

    /// <summary>
    /// The default JSON Schema dialect for schema definitions. 2025-11-25 established
    /// JSON Schema 2020-12 as the default (SEP-1613); earlier revisions did not mandate one.
    /// </summary>
    public string DefaultSchemaDialect => AtLeast(V2025_11_25)
        ? "https://json-schema.org/draft/2020-12/schema"
        : "https://json-schema.org/draft-07/schema";

    public override string ToString() => Version;
}
