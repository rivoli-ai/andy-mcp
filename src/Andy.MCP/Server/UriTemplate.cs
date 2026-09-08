using System.Text;
using System.Text.RegularExpressions;

namespace Andy.MCP.Server;

/// <summary>RFC 6570 resource-template matching. Composite values retain their decoded separators.</summary>
public sealed class UriTemplate
{
    private sealed record Variable(string Name, bool Explode, int? Prefix);
    private sealed record Expression(char Operator, Variable[] Variables, string Group);
    private readonly Regex _matcher;
    private readonly List<Expression> _expressions = [];
    public string Template { get; }
    public IReadOnlyList<string> Variables { get; }
    private const string Unreserved = @"(?:[A-Za-z0-9_~.\-]|%[0-9A-Fa-f]{2})";

    public UriTemplate(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.Length > 4096) throw new ArgumentException("URI template exceeds 4096 characters.", nameof(template));
        Template = template;
        if (template.Any(char.IsWhiteSpace) || template.Any(char.IsControl))
            throw new ArgumentException("Whitespace is not permitted in URI templates.", nameof(template));
        var pattern = new StringBuilder("\\A");
        var names = new List<string>();
        for (var offset = 0; offset < template.Length;)
        {
            if (template[offset] == '}') throw new ArgumentException("Unmatched template brace.", nameof(template));
            if (template[offset] != '{')
            {
                pattern.Append(Regex.Escape(template[offset++].ToString()));
                continue;
            }
            var end = template.IndexOf('}', offset + 1);
            if (end < 0) throw new ArgumentException("Unclosed template expression.", nameof(template));
            var body = template[(offset + 1)..end];
            var op = body.Length > 0 && "+#./;?&".Contains(body[0]) ? body[0] : '\0';
            if (op != '\0') body = body[1..];
            var variables = new List<Variable>();
            foreach (var spec in body.Split(','))
            {
                var parsed = Regex.Match(spec, @"\A(?<name>(?:[A-Za-z0-9_]|%[0-9A-Fa-f]{2})+(?:\.(?:[A-Za-z0-9_]|%[0-9A-Fa-f]{2})+)*)(?:(?<explode>\*)|:(?<prefix>[1-9][0-9]{0,3}))?\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                if (!parsed.Success) throw new ArgumentException($"Invalid URI-template variable: '{spec}'.", nameof(template));
                var name = parsed.Groups["name"].Value;
                variables.Add(new Variable(name, parsed.Groups["explode"].Success,
                    parsed.Groups["prefix"].Success ? int.Parse(parsed.Groups["prefix"].Value) : null));
                if (!names.Contains(name, StringComparer.Ordinal)) names.Add(name);
            }
            if (_expressions.Count >= 128) throw new ArgumentException("Too many URI-template expressions.", nameof(template));
            var group = "e" + _expressions.Count;
            _expressions.Add(new Expression(op, variables.ToArray(), group));
            var atom = op switch
            {
                '+' or '#' => @"(?:[A-Za-z0-9_~.\-:#/?\[\]@!$&'()*+,;=]|%[0-9A-Fa-f]{2})",
                '?' or '&' => "(?:" + Unreserved + "|[=&,])",
                ';' => "(?:" + Unreserved + "|[;=,])",
                '/' => "(?:" + Unreserved + "|[/,=])",
                _ => "(?:" + Unreserved + "|[,=])"
            };
            var capture = $"(?<{group}>{atom}*)";
            pattern.Append(op is '\0' or '+' ? capture : "(?:" + Regex.Escape(op.ToString()) + capture + ")?");
            offset = end + 1;
        }
        pattern.Append("\\z");
        _matcher = new Regex(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
        Variables = names.AsReadOnly();
    }

    public bool TryMatch(string uri, out IReadOnlyDictionary<string, string> variables)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        variables = result;
        if (uri is null || uri.Length > 32768) return false;
        Match match;
        try { match = _matcher.Match(uri); }
        catch (RegexMatchTimeoutException) { return false; }
        if (!match.Success) return false;
        var prefixes = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var expression in _expressions)
        {
            var group = match.Groups[expression.Group];
            if (!group.Success) continue;
            if (!Extract(expression, group.Value, result, prefixes)) { result.Clear(); return false; }
        }
        return true;
    }

    private static bool Extract(Expression expression, string value, Dictionary<string, string> result, Dictionary<string, bool> prefixes)
    {
        var separator = expression.Operator switch { '?' or '&' => '&', ';' => ';', '/' => '/', '.' => '.', _ => ',' };
        var named = expression.Operator is '?' or '&' or ';';
        var parts = value.Split(separator);
        if (named)
        {
            var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var part in parts)
            {
                if (part.Length == 0) continue;
                var equals = part.IndexOf('=');
                var key = equals < 0 ? part : part[..equals];
                var spec = expression.Variables.FirstOrDefault(v => v.Name == key);
                var raw = equals < 0 ? "" : part[(equals + 1)..];
                if (spec is null)
                {
                    spec = expression.Variables.FirstOrDefault(v => v.Explode);
                    if (spec is null) return false;
                    raw = part; // Exploded associative entries retain their keys.
                }
                if (!values.TryGetValue(spec.Name, out var entries)) values[spec.Name] = entries = [];
                if (entries.Count > 0 && !spec.Explode) return false;
                entries.Add(raw);
            }
            foreach (var spec in expression.Variables)
                if (values.TryGetValue(spec.Name, out var entries) && !Store(spec, string.Join(separator, entries), result, prefixes)) return false;
            return true;
        }
        var index = 0;
        for (var i = 0; i < expression.Variables.Length && index < parts.Length; i++)
        {
            var spec = expression.Variables[i];
            var count = spec.Explode || i == expression.Variables.Length - 1 ? Math.Max(1, parts.Length - index - (expression.Variables.Length - i - 1)) : 1;
            if (!Store(spec, string.Join(separator, parts.Skip(index).Take(count)), result, prefixes)) return false;
            index += count;
        }
        return index == parts.Length;
    }

    private static bool Store(Variable spec, string raw, Dictionary<string, string> result, Dictionary<string, bool> prefixes)
    {
        var decoded = Uri.UnescapeDataString(raw);
        if (spec.Prefix is { } max && decoded.EnumerateRunes().Count() > max) return false;
        if (result.TryGetValue(spec.Name, out var prior))
        {
            if (spec.Prefix is not null && prior.StartsWith(decoded, StringComparison.Ordinal)) return true;
            if (!prefixes[spec.Name] || !decoded.StartsWith(prior, StringComparison.Ordinal)) return prior == decoded;
        }
        result[spec.Name] = decoded;
        prefixes[spec.Name] = spec.Prefix is not null;
        return true;
    }
}
