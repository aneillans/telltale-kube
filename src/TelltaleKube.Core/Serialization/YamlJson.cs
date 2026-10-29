using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace TelltaleKube.Core.Serialization;

/// <summary>
/// Reflection-free YAML ⇄ JSON conversion for the resource editor. Hardened against alias expansion
/// ("billion laughs") and pathological nesting.
/// </summary>
public static partial class YamlJson
{
    private const int MaxNodes = 500_000;
    private const int MaxDepth = 128;

    public static string ToYaml(JsonNode node)
    {
        var doc = new YamlDocument(ToYamlNode(node));
        using var sw = new StringWriter();
        new YamlStream(doc).Save(sw, assignAnchors: false);
        var text = sw.ToString();
        // YamlStream terminates documents with "...", which is noise in an editor.
        return text.EndsWith("...\n", StringComparison.Ordinal) ? text[..^4] : text;
    }

    public static IReadOnlyList<JsonObject> FromYaml(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            throw new FormatException($"Invalid YAML (line {ex.Start.Line}): {ex.Message}", ex);
        }

        var result = new List<JsonObject>();
        var budget = new Budget();
        foreach (var doc in stream.Documents)
        {
            if (doc.RootNode is YamlScalarNode { Value: null or "" }) continue;
            if (ToJson(doc.RootNode, budget, 0) is JsonObject o) result.Add(o);
            else throw new FormatException("Each YAML document must be a mapping (a Kubernetes object).");
        }
        return result;
    }

    public static JsonObject FromYamlSingle(string yaml)
    {
        var docs = FromYaml(yaml);
        return docs.Count == 1 ? docs[0] : throw new FormatException($"Expected exactly one YAML document, found {docs.Count}.");
    }

    private sealed class Budget
    {
        public int Nodes;
    }

    private static JsonNode? ToJson(YamlNode node, Budget budget, int depth)
    {
        if (++budget.Nodes > MaxNodes) throw new FormatException("YAML document is too large (possible alias bomb).");
        if (depth > MaxDepth) throw new FormatException("YAML document is nested too deeply.");

        switch (node)
        {
            case YamlMappingNode m:
                var obj = new JsonObject();
                foreach (var (k, v) in m.Children)
                {
                    var key = (k as YamlScalarNode)?.Value ?? throw new FormatException("Only scalar mapping keys are supported.");
                    obj[key] = ToJson(v, budget, depth + 1);
                }
                return obj;
            case YamlSequenceNode s:
                var arr = new JsonArray();
                foreach (var item in s.Children) arr.Add(ToJson(item, budget, depth + 1));
                return arr;
            case YamlScalarNode sc:
                return ScalarToJson(sc);
            default:
                return null;
        }
    }

    private static JsonValue? ScalarToJson(YamlScalarNode sc)
    {
        var value = sc.Value ?? "";
        if (sc.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted or ScalarStyle.Literal or ScalarStyle.Folded)
        {
            return JsonValue.Create(value);
        }
        if (!sc.Tag.IsEmpty && sc.Tag.Value == "tag:yaml.org,2002:str") return JsonValue.Create(value);

        switch (value)
        {
            case "" or "~" or "null" or "Null" or "NULL": return null;
            case "true" or "True" or "TRUE": return JsonValue.Create(true);
            case "false" or "False" or "FALSE": return JsonValue.Create(false);
        }
        if (IntRegex().IsMatch(value) && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
        if (FloatRegex().IsMatch(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d);
        return JsonValue.Create(value);
    }

    private static YamlNode ToYamlNode(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return new YamlScalarNode("null") { Style = ScalarStyle.Plain };
            case JsonObject o:
                var m = new YamlMappingNode();
                foreach (var (k, v) in o) m.Add(new YamlScalarNode(k), ToYamlNode(v));
                return m;
            case JsonArray a:
                var s = new YamlSequenceNode();
                foreach (var item in a) s.Add(ToYamlNode(item));
                return s;
            case JsonValue v:
                switch (v.GetValueKind())
                {
                    case JsonValueKind.String:
                        var str = v.GetValue<string>();
                        return new YamlScalarNode(str) { Style = StringStyle(str) };
                    case JsonValueKind.True: return new YamlScalarNode("true");
                    case JsonValueKind.False: return new YamlScalarNode("false");
                    case JsonValueKind.Null: return new YamlScalarNode("null");
                    default: return new YamlScalarNode(v.ToJsonString());
                }
            default:
                return new YamlScalarNode(node.ToJsonString());
        }
    }

    /// <summary>Strings that a YAML parser would read back as a different type (or that need escaping) are quoted.</summary>
    private static ScalarStyle StringStyle(string s)
    {
        if (s.Contains('\n', StringComparison.Ordinal)) return s.Any(c => char.IsControl(c) && c is not '\n' and not '\t') ? ScalarStyle.DoubleQuoted : ScalarStyle.Literal;
        if (s.Length == 0 || ScalarToJson(new YamlScalarNode(s)) is not { } jv || jv.GetValueKind() != JsonValueKind.String) return ScalarStyle.DoubleQuoted;
        if (AmbiguousRegex().IsMatch(s) || s.Any(char.IsControl)) return ScalarStyle.DoubleQuoted;
        return ScalarStyle.Any;
    }

    [GeneratedRegex(@"^[-+]?[0-9]+$")]
    private static partial Regex IntRegex();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex FloatRegex();

    // YAML 1.1 booleans (yes/no/on/off), octal/hex/sexagesimal-looking values, and leading indicators.
    [GeneratedRegex(@"^(y|Y|yes|Yes|YES|n|N|no|No|NO|on|On|ON|off|Off|OFF|0[xXoO]?[0-9a-fA-F_]+|[0-9]+(:[0-9]+)+|\.inf|\.nan|[\s\-?:,\[\]{}#&*!|>'""%@`].*|.*\s)$")]
    private static partial Regex AmbiguousRegex();
}
