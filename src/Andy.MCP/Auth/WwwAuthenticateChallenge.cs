using System.Text.RegularExpressions;

namespace Andy.MCP.Auth;

/// <summary>
/// A parsed Bearer <c>WWW-Authenticate</c> challenge (RFC 6750 / MCP 2025-11-25 authorization),
/// exposing the well-known auth parameters used for discovery and step-up consent.
/// </summary>
public sealed record WwwAuthenticateChallenge
{
    private static readonly Regex ParamPattern = new(
        """(?<key>[A-Za-z0-9_-]+)\s*=\s*(?:"(?<qval>[^"]*)"|(?<val>[^,\s]+))""",
        RegexOptions.Compiled);

    public required string Scheme { get; init; }

    public required IReadOnlyDictionary<string, string> Parameters { get; init; }

    /// <summary>The OAuth error code, e.g. <c>invalid_token</c> or <c>insufficient_scope</c>.</summary>
    public string? Error => Get("error");

    public string? ErrorDescription => Get("error_description");

    /// <summary>URL of the protected-resource metadata document (RFC 9728).</summary>
    public string? ResourceMetadata => Get("resource_metadata");

    public string? Realm => Get("realm");

    /// <summary>Space-delimited scope value from a scope/step-up challenge.</summary>
    public string? Scope => Get("scope");

    /// <summary>The individual scopes requested by the challenge.</summary>
    public IReadOnlyList<string> Scopes =>
        Scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    private string? Get(string key) => Parameters.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// Parse the first Bearer challenge from a <c>WWW-Authenticate</c> header value. Returns false
    /// if the header is absent or is not a Bearer challenge.
    /// </summary>
    public static bool TryParse(string? header, out WwwAuthenticateChallenge challenge)
    {
        challenge = null!;
        if (string.IsNullOrWhiteSpace(header))
            return false;

        header = header.Trim();
        const string scheme = "Bearer";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = header.Length > scheme.Length ? header[scheme.Length..] : string.Empty;
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in ParamPattern.Matches(rest))
        {
            var key = match.Groups["key"].Value;
            var value = match.Groups["qval"].Success ? match.Groups["qval"].Value : match.Groups["val"].Value;
            parameters[key] = value;
        }

        challenge = new WwwAuthenticateChallenge { Scheme = scheme, Parameters = parameters };
        return true;
    }
}
