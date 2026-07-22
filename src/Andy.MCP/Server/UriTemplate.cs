using System.Text;
using System.Text.RegularExpressions;

namespace Andy.MCP.Server;

/// <summary>
/// A minimal RFC 6570 URI template matcher for resource templates. Supports level-1 simple
/// expansion <c>{var}</c> (matches a single path segment, no '/') and the reserved operator
/// <c>{+var}</c> (matches any characters, including '/'). This covers the forms MCP resource
/// templates use in practice.
/// </summary>
public sealed class UriTemplate
{
    private static readonly Regex PlaceholderPattern = new(@"\{(?<op>\+?)(?<name>[A-Za-z0-9_]+)\}", RegexOptions.Compiled);

    private readonly Regex _matcher;

    public string Template { get; }

    /// <summary>The variable names declared by the template, in order.</summary>
    public IReadOnlyList<string> Variables { get; }

    public UriTemplate(string template)
    {
        Template = template ?? throw new ArgumentNullException(nameof(template));

        var pattern = new StringBuilder("^");
        var variables = new List<string>();
        var last = 0;

        foreach (Match placeholder in PlaceholderPattern.Matches(template))
        {
            // Literal text before this placeholder.
            pattern.Append(Regex.Escape(template[last..placeholder.Index]));

            var name = placeholder.Groups["name"].Value;
            var reserved = placeholder.Groups["op"].Value == "+";
            variables.Add(name);
            // Reserved expansion may include '/', simple expansion is a single segment.
            pattern.Append($"(?<{name}>{(reserved ? ".+" : "[^/]+")})");

            last = placeholder.Index + placeholder.Length;
        }

        pattern.Append(Regex.Escape(template[last..]));
        pattern.Append('$');

        _matcher = new Regex(pattern.ToString(), RegexOptions.Compiled);
        Variables = variables;
    }

    /// <summary>
    /// Try to match a concrete URI against the template, extracting (URL-decoded) variable values.
    /// </summary>
    public bool TryMatch(string uri, out IReadOnlyDictionary<string, string> variables)
    {
        var match = _matcher.Match(uri);
        if (!match.Success)
        {
            variables = new Dictionary<string, string>();
            return false;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in Variables)
            result[name] = Uri.UnescapeDataString(match.Groups[name].Value);

        variables = result;
        return true;
    }
}
