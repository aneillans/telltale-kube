using System.Globalization;
using System.Text.Json.Nodes;

namespace TelltaleKube.Core.Resources;

/// <summary>
/// A thin, schema-less view over any Kubernetes object. Keeping objects as JSON (instead of thousands of
/// generated model classes) is what keeps Telltale Kube small and lets it browse any CRD.
/// </summary>
public sealed class KubeObject(JsonObject json)
{
    public JsonObject Json { get; } = json;

    public string ApiVersion => Str("apiVersion") ?? "";
    public string Kind => Str("kind") ?? "";
    public string Name => Meta("name") ?? "";
    public string? Namespace => Meta("namespace");
    public string Uid => Meta("uid") ?? "";
    public string? ResourceVersion => Meta("resourceVersion");
    public DateTimeOffset? Created =>
        DateTimeOffset.TryParse(Meta("creationTimestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    public bool IsTerminating => Json["metadata"]?["deletionTimestamp"] is not null;

    public IReadOnlyDictionary<string, string> Labels => StringMap(Json["metadata"]?["labels"]);
    public IReadOnlyDictionary<string, string> Annotations => StringMap(Json["metadata"]?["annotations"]);

    public JsonNode? this[string path] => Path(Json, path);

    /// <summary>Navigates a dotted path (<c>status.containerStatuses.0.ready</c>).</summary>
    public static JsonNode? Path(JsonNode? node, string path)
    {
        foreach (var part in path.Split('.'))
        {
            node = node switch
            {
                JsonObject o => o[part],
                JsonArray a when int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < a.Count => a[i],
                _ => null,
            };
            if (node is null) return null;
        }
        return node;
    }

    public string? GetString(string path) => this[path] is JsonValue v ? v.ToString() : null;

    public long? GetLong(string path) =>
        this[path] is JsonValue v && long.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null;

    private string? Str(string key) => Json[key] is JsonValue v ? v.ToString() : null;
    private string? Meta(string key) => Json["metadata"]?[key] is JsonValue v ? v.ToString() : null;

    private static Dictionary<string, string> StringMap(JsonNode? node)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is JsonObject o)
        {
            foreach (var (k, v) in o) d[k] = v?.ToString() ?? "";
        }
        return d;
    }

    public override string ToString() => Namespace is null ? $"{Kind}/{Name}" : $"{Kind}/{Namespace}/{Name}";
}
