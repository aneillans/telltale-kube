using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace TelltaleKube.Core.Demo;

/// <summary>Answers Kubernetes API reads from sample data, in process, so the demo cluster makes no network calls.</summary>
internal sealed class DemoHandler : HttpMessageHandler
{
    public static readonly Uri Server = new("https://demo.invalid/");

    /// <summary>Increments on every request so metrics change between samples.</summary>
    private int _tick;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(Route(request));

    private HttpResponseMessage Route(HttpRequestMessage request)
    {
        if (request.Method != HttpMethod.Get) return Status(HttpStatusCode.Forbidden, "The demo cluster is read-only.");
        var uri = request.RequestUri!;
        if (Query(uri, "watch") is "1" or "true") return IdleStream();
        return uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) switch
        {
            ["version"] => Json(new JsonObject { ["gitVersion"] = "v1.34.1" }),
            ["apis"] => Json(DemoFixtures.ApiGroups()),
            ["apis", "cert-manager.io", "v1"] => Json(DemoFixtures.CertManagerResources()),
            ["api", _, .. var rest] => Resource(uri, rest, metrics: false),
            ["apis", "metrics.k8s.io", _, .. var rest] => Resource(uri, rest, metrics: true),
            ["apis", _, _, .. var rest] => Resource(uri, rest, metrics: false),
            _ => NotFound(),
        };
    }

    private HttpResponseMessage Resource(Uri uri, string[] rest, bool metrics)
    {
        string? ns = null;
        if (rest is ["namespaces", var scope, _, ..])
        {
            ns = scope;
            rest = rest[2..];
        }
        if (rest.Length == 0) return NotFound();

        var items = (metrics ? DemoFixtures.Metrics(rest[0], Interlocked.Increment(ref _tick)) : DemoFixtures.Items(rest[0]))
            .Where(i => ns is null || Field(i, "metadata.namespace") == ns);
        if (rest.Length == 1)
        {
            var labels = Query(uri, "labelSelector");
            var fields = Query(uri, "fieldSelector");
            return Json(new JsonObject
            {
                ["kind"] = "List",
                ["metadata"] = new JsonObject { ["resourceVersion"] = "100" },
                ["items"] = new JsonArray(items.Where(i => MatchesLabels(i, labels) && MatchesFields(i, fields)).ToArray()),
            });
        }

        var item = items.FirstOrDefault(i => Field(i, "metadata.name") == rest[1]);
        if (item is null) return NotFound();
        return rest switch
        {
            [_, _] => Json(item),
            [_, var pod, "log"] => Logs(uri, pod),
            [_, _, "proxy", ..] => Status(HttpStatusCode.Forbidden, "nodes/proxy is not available on the demo cluster."),
            _ => NotFound(),
        };
    }

    private static HttpResponseMessage Logs(Uri uri, string pod)
    {
        if (Query(uri, "previous") == "true" && !pod.StartsWith("worker", StringComparison.Ordinal))
        {
            return Status(HttpStatusCode.BadRequest, $"previous terminated container \"app\" in pod \"{pod}\" not found");
        }
        var lines = DemoFixtures.LogLines(pod);
        var timestamps = Query(uri, "timestamps") == "true";
        var now = DateTimeOffset.UtcNow;
        var text = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            if (timestamps) text.Append(now.AddSeconds((i - lines.Length) * 7).ToString("O", System.Globalization.CultureInfo.InvariantCulture)).Append(' ');
            text.Append(lines[i]).Append('\n');
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text.ToString(), Encoding.UTF8, "text/plain") };
    }

    /// <summary>A watch that delivers nothing and stays open until the client cancels: the sample data never changes.</summary>
    private static HttpResponseMessage IdleStream() =>
        new(HttpStatusCode.OK) { Content = new StreamContent(new Pipe().Reader.AsStream()) };

    private static bool MatchesLabels(JsonObject item, string? selector) =>
        selector is null || selector.Split(',', StringSplitOptions.RemoveEmptyEntries).All(term =>
        {
            var parts = term.Split('=', 2);
            return parts.Length == 2 && (item["metadata"]?["labels"] as JsonObject)?[parts[0]]?.ToString() == parts[1];
        });

    private static bool MatchesFields(JsonObject item, string? selector) =>
        selector is null || selector.Split(',', StringSplitOptions.RemoveEmptyEntries).All(term =>
        {
            var negate = term.Contains("!=", StringComparison.Ordinal);
            var parts = term.Split(negate ? "!=" : "=", 2);
            return parts.Length == 2 && (Field(item, parts[0]) == parts[1]) != negate;
        });

    private static string? Field(JsonNode node, string path)
    {
        JsonNode? current = node;
        foreach (var part in path.Split('.')) current = (current as JsonObject)?[part];
        return current?.ToString();
    }

    private static string? Query(Uri uri, string key)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts[0] == key) return parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "";
        }
        return null;
    }

    private static HttpResponseMessage Json(JsonNode body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Status(HttpStatusCode status, string message) =>
        Json(new JsonObject { ["kind"] = "Status", ["message"] = message }, status);

    private static HttpResponseMessage NotFound() => Status(HttpStatusCode.NotFound, "not found");
}
