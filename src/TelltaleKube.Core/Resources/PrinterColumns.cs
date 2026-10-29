using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace TelltaleKube.Core.Resources;

/// <summary>A CRD's <c>additionalPrinterColumns</c> entry (what <c>kubectl get</c> shows for the type).</summary>
public sealed record PrinterColumn(string Name, string Type, string JsonPath, int Priority = 0, string? Description = null);

/// <summary>
/// Reads printer columns from CustomResourceDefinitions and evaluates their JSONPath against objects.
/// Supports the subset CRDs use in practice: <c>.a.b</c>, escaped dots (<c>.a\.b</c>), <c>['key']</c>, <c>[0]</c>,
/// <c>[-1]</c>, <c>[*]</c> and filters such as <c>[?(@.type=="Ready")]</c> (<c>==</c> / <c>!=</c>).
/// </summary>
public static class PrinterColumns
{
    public static Dictionary<string, IReadOnlyList<PrinterColumn>> FromCrds(JsonNode? crdList, IReadOnlyDictionary<string, string> versionByKey)
    {
        var result = new Dictionary<string, IReadOnlyList<PrinterColumn>>(StringComparer.Ordinal);
        foreach (var crd in crdList?["items"]?.AsArray() ?? [])
        {
            var group = crd?["spec"]?["group"]?.ToString();
            var plural = crd?["spec"]?["names"]?["plural"]?.ToString();
            if (group is null || plural is null) continue;
            var key = $"{plural}.{group}";
            var versions = crd?["spec"]?["versions"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
            var version = versionByKey.TryGetValue(key, out var v) ? versions.FirstOrDefault(x => x["name"]?.ToString() == v) : null;
            version ??= versions.FirstOrDefault(x => x["storage"]?.GetValueKind() == System.Text.Json.JsonValueKind.True) ?? versions.FirstOrDefault();
            // apiextensions v1beta1 kept columns at spec level; v1 keeps them per version.
            var columns = (version?["additionalPrinterColumns"] ?? crd?["spec"]?["additionalPrinterColumns"])?.AsArray();
            var parsed = new List<PrinterColumn>();
            foreach (var c in columns ?? [])
            {
                var name = c?["name"]?.ToString();
                var path = (c?["jsonPath"] ?? c?["JSONPath"])?.ToString();
                if (name is null || path is null) continue;
                // The list already has Name and Age.
                if (path.Trim() is ".metadata.creationTimestamp" or ".metadata.name" or ".metadata.namespace" || name is "Name" or "Namespace" or "Age") continue;
                parsed.Add(new PrinterColumn(name, c?["type"]?.ToString() ?? "string", path,
                    c?["priority"] is JsonValue p && p.TryGetValue(out int pr) ? pr : 0, c?["description"]?.ToString()));
            }
            result[key] = parsed;
        }
        return result;
    }

    /// <summary>List columns for printer columns: priority 0 first, then wide columns, up to <paramref name="max"/>.</summary>
    public static IReadOnlyList<Column> ToColumns(IReadOnlyList<PrinterColumn> columns, int max = 8) =>
        columns.OrderBy(c => c.Priority > 0 ? 1 : 0).Take(max).Select(c => new Column(c.Name, o => Format(c, o), IsFreeText(c) ? 2.5 : 1)).ToList();

    private static bool IsFreeText(PrinterColumn c) =>
        c.Type == "string" && (c.JsonPath.TrimEnd('}').EndsWith(".message", StringComparison.Ordinal) || c.Name is "Message" or "Status" or "Reason" or "Description");

    /// <summary>Cell text for one object, formatted by column type (dates become ages, like kubectl).</summary>
    public static string Format(PrinterColumn column, KubeObject o)
    {
        var values = Evaluate(o.Json, column.JsonPath);
        if (values.Count == 0) return "";
        if (column.Type == "date")
        {
            return string.Join(',', values.Select(v => DateTimeOffset.TryParse(Text(v), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
                ? d > DateTimeOffset.UtcNow ? "in " + ResourcePresenter.FormatAge(d - DateTimeOffset.UtcNow) : ResourcePresenter.FormatAge(DateTimeOffset.UtcNow - d)
                : Text(v)));
        }
        return string.Join(',', values.Select(Text));
    }

    private static string Text(JsonNode? n) => n switch
    {
        null => "",
        JsonValue v when v.TryGetValue(out string? s) => s,
        JsonValue v when v.TryGetValue(out bool b) => b ? "true" : "false",
        JsonValue v => v.ToJsonString(),
        _ => n.ToJsonString(),
    };

    public static IReadOnlyList<JsonNode?> Evaluate(JsonNode? root, string path)
    {
        var p = path.Trim();
        if (p.StartsWith('{') && p.EndsWith('}')) p = p[1..^1].Trim();
        if (p.StartsWith('$')) p = p[1..];
        List<JsonNode?> current = [root];
        var i = 0;
        try
        {
            while (i < p.Length && current.Count > 0)
            {
                if (p[i] == '.')
                {
                    i++;
                    if (i < p.Length && p[i] == '.') return []; // recursive descent: not supported
                    var name = ReadName(p, ref i);
                    if (name.Length == 0) continue;
                    current = name == "*" ? current.SelectMany(Children).ToList() : current.Select(n => (n as JsonObject)?[name]).Where(n => n is not null).ToList();
                }
                else if (p[i] == '[')
                {
                    var end = FindClose(p, i);
                    var inner = p[(i + 1)..end].Trim();
                    i = end + 1;
                    current = Bracket(current, inner);
                }
                else
                {
                    current = current.Select(n => (n as JsonObject)?[ReadName(p, ref i)]).Where(n => n is not null).ToList();
                }
            }
        }
        catch (FormatException)
        {
            return [];
        }
        return current;
    }

    private static IEnumerable<JsonNode?> Children(JsonNode? n) => n switch
    {
        JsonArray a => a,
        JsonObject o => o.Select(kv => kv.Value),
        _ => [],
    };

    private static string ReadName(string p, ref int i)
    {
        var sb = new StringBuilder();
        while (i < p.Length && p[i] is not ('.' or '['))
        {
            if (p[i] == '\\' && i + 1 < p.Length) i++;
            sb.Append(p[i++]);
        }
        return sb.ToString();
    }

    private static int FindClose(string p, int open)
    {
        var depth = 0;
        char? quote = null;
        for (var j = open; j < p.Length; j++)
        {
            var c = p[j];
            if (quote is not null)
            {
                if (c == quote) quote = null;
            }
            else if (c is '\'' or '"') quote = c;
            else if (c == '[') depth++;
            else if (c == ']' && --depth == 0) return j;
        }
        throw new FormatException("Unclosed bracket");
    }

    private static List<JsonNode?> Bracket(List<JsonNode?> current, string inner)
    {
        if (inner == "*") return current.SelectMany(Children).ToList();
        if (inner.Length >= 2 && inner[0] is '\'' or '"') return current.Select(n => (n as JsonObject)?[inner[1..^1]]).Where(n => n is not null).ToList();
        if (int.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            return current.OfType<JsonArray>().Select(a => index < 0 ? (a.Count + index >= 0 ? a[a.Count + index] : null) : index < a.Count ? a[index] : null).Where(n => n is not null).ToList();
        }
        if (inner.StartsWith("?(", StringComparison.Ordinal) && inner.EndsWith(')'))
        {
            var expr = inner[2..^1].Trim();
            var (op, at) = expr.IndexOf("!=", StringComparison.Ordinal) is var ne and >= 0 ? ("!=", ne) : ("==", expr.IndexOf("==", StringComparison.Ordinal));
            string field;
            string? expected = null;
            if (at < 0)
            {
                field = expr; // existence test: [?(@.ready)]
            }
            else
            {
                field = expr[..at].Trim();
                expected = expr[(at + 2)..].Trim().Trim('"', '\'');
            }
            if (!field.StartsWith('@')) throw new FormatException("Unsupported filter");
            var fieldPath = field[1..];
            return current.SelectMany(Children).Where(item =>
            {
                var found = Evaluate(item, fieldPath);
                if (expected is null) return found.Count > 0;
                var match = found.Any(f => string.Equals(Text(f), expected, StringComparison.Ordinal));
                return op == "==" ? match : !match;
            }).ToList();
        }
        throw new FormatException("Unsupported index");
    }
}
