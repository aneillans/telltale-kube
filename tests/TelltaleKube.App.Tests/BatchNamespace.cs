using System.Text.Json.Nodes;

namespace TelltaleKube.App.Tests;

/// <summary>
/// The "batch" namespace of the fake API server: a CronJob with three runs (succeeded, failed after two attempts,
/// running), their pods, and a Deployment → ReplicaSet → Pod chain. Supports label selectors, get, create and patch.
/// </summary>
internal sealed class BatchNamespace
{
    private const string Ns = "batch";
    private readonly Dictionary<string, List<JsonObject>> _objects = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public BatchNamespace()
    {
        var now = DateTimeOffset.UtcNow;
        var cron = Obj("backup", "cron-uid", now.AddDays(-30));
        cron["spec"] = new JsonObject
        {
            ["schedule"] = "0 2 * * *",
            ["jobTemplate"] = new JsonObject
            {
                ["spec"] = new JsonObject
                {
                    ["backoffLimit"] = 1,
                    ["template"] = new JsonObject { ["spec"] = new JsonObject { ["restartPolicy"] = "Never", ["containers"] = new JsonArray(new JsonObject { ["name"] = "backup", ["image"] = "backup:1" }) } },
                },
            },
        };
        cron["status"] = new JsonObject
        {
            ["lastScheduleTime"] = Ts(now.AddMinutes(-10)),
            ["lastSuccessfulTime"] = Ts(now.AddDays(-2).AddMinutes(4)),
            ["active"] = new JsonArray(new JsonObject { ["name"] = "backup-29000003" }),
        };
        Add("cronjobs", cron);

        Job("backup-29000001", "job1-uid", now.AddDays(-2), "Complete", null, 0, [("backup-29000001-abcde", "Succeeded")]);
        Job("backup-29000002", "job2-uid", now.AddDays(-1), "Failed", "BackoffLimitExceeded", 2, [("backup-29000002-fghij", "Failed"), ("backup-29000002-klmno", "Failed")]);
        Job("backup-29000003", "job3-uid", now.AddMinutes(-10), null, null, 0, [("backup-29000003-pqrst", "Running")]);

        var deploy = Obj("api", "deploy-uid", now.AddDays(-3));
        deploy["spec"] = new JsonObject { ["replicas"] = 1, ["selector"] = new JsonObject { ["matchLabels"] = new JsonObject { ["app"] = "api" } } };
        deploy["status"] = new JsonObject { ["readyReplicas"] = 1 };
        Add("deployments", deploy);
        var rs = Obj("api-7d9f8", "rs-uid", now.AddDays(-3), Owner("apps/v1", "Deployment", "api", "deploy-uid"));
        rs["spec"] = new JsonObject { ["replicas"] = 1, ["selector"] = new JsonObject { ["matchLabels"] = new JsonObject { ["app"] = "api", ["pod-template-hash"] = "7d9f8" } } };
        Add("replicasets", rs);
        Add("pods", PodObj("api-7d9f8-xk2p4", now.AddHours(-5), "Running", "api", Owner("apps/v1", "ReplicaSet", "api-7d9f8", "rs-uid"),
            new JsonObject { ["app"] = "api", ["pod-template-hash"] = "7d9f8" }));
    }

    public List<JsonObject> Created { get; } = [];

    public (int, JsonNode) Route(string method, string path, string? labelSelector, string? body)
    {
        // /api/v1/namespaces/batch/pods[/name]  or  /apis/<group>/<version>/namespaces/batch/<plural>[/name]
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var nsIndex = Array.IndexOf(segments, "namespaces");
        var plural = segments[nsIndex + 2];
        var name = segments.Length > nsIndex + 3 ? segments[nsIndex + 3] : null;

        lock (_lock)
        {
            var items = _objects.GetValueOrDefault(plural) ?? [];
            if (method == "POST" && name is null && body is not null)
            {
                var created = JsonNode.Parse(body)!.AsObject();
                created["metadata"]!["uid"] = Guid.NewGuid().ToString();
                created["metadata"]!["creationTimestamp"] = Ts(DateTimeOffset.UtcNow);
                created["status"] = new JsonObject { ["active"] = 1, ["startTime"] = Ts(DateTimeOffset.UtcNow) };
                Add(plural, created);
                Created.Add(created);
                return (201, created);
            }
            if (name is null)
            {
                var matching = items.Where(o => Matches(o, labelSelector)).Select(o => (JsonNode)o.DeepClone()).ToArray();
                return (200, new JsonObject { ["kind"] = "List", ["metadata"] = new JsonObject { ["resourceVersion"] = "7" }, ["items"] = new JsonArray(matching) });
            }
            var obj = items.FirstOrDefault(o => o["metadata"]!["name"]!.ToString() == name);
            if (obj is null) return (404, new JsonObject { ["kind"] = "Status", ["message"] = $"{plural} \"{name}\" not found" });
            if (method == "PATCH" && body is not null && JsonNode.Parse(body)?["spec"] is JsonObject specPatch)
            {
                foreach (var (k, v) in specPatch) obj["spec"]![k] = v?.DeepClone();
            }
            return (200, obj.DeepClone());
        }
    }

    private static bool Matches(JsonObject o, string? selector)
    {
        if (string.IsNullOrEmpty(selector)) return true;
        var labels = o["metadata"]?["labels"] as JsonObject;
        return selector.Split(',').All(term =>
        {
            var kv = term.Split('=', 2);
            return kv.Length == 2 && labels?[kv[0]]?.ToString() == kv[1];
        });
    }

    private void Job(string name, string uid, DateTimeOffset start, string? condition, string? reason, int failed, (string Pod, string Phase)[] pods)
    {
        var job = Obj(name, uid, start, Owner("batch/v1", "CronJob", "backup", "cron-uid"));
        job["metadata"]!["labels"] = new JsonObject { ["batch.kubernetes.io/controller-uid"] = uid, ["batch.kubernetes.io/job-name"] = name };
        job["spec"] = new JsonObject
        {
            ["completions"] = 1,
            ["selector"] = new JsonObject { ["matchLabels"] = new JsonObject { ["batch.kubernetes.io/controller-uid"] = uid } },
        };
        var status = new JsonObject { ["startTime"] = Ts(start), ["failed"] = failed };
        if (condition is not null)
        {
            status["conditions"] = new JsonArray(new JsonObject { ["type"] = condition, ["status"] = "True", ["reason"] = reason, ["lastTransitionTime"] = Ts(start.AddMinutes(3)) });
            if (condition == "Complete")
            {
                status["completionTime"] = Ts(start.AddMinutes(3).AddSeconds(25));
                status["succeeded"] = 1;
            }
        }
        else
        {
            status["active"] = 1;
        }
        job["status"] = status;
        Add("jobs", job);

        var i = 0;
        foreach (var (podName, phase) in pods)
        {
            Add("pods", PodObj(podName, start.AddSeconds(30 * i++), phase, "backup", Owner("batch/v1", "Job", name, uid),
                new JsonObject { ["batch.kubernetes.io/controller-uid"] = uid, ["batch.kubernetes.io/job-name"] = name }));
        }
    }

    private static JsonObject PodObj(string name, DateTimeOffset created, string phase, string container, JsonObject owner, JsonObject labels)
    {
        var pod = Obj(name, Guid.NewGuid().ToString(), created, owner);
        pod["metadata"]!["labels"] = labels;
        pod["spec"] = new JsonObject { ["nodeName"] = "node-a", ["containers"] = new JsonArray(new JsonObject { ["name"] = container, ["image"] = $"{container}:1" }) };
        var state = phase switch
        {
            "Running" => new JsonObject { ["running"] = new JsonObject() },
            "Succeeded" => new JsonObject { ["terminated"] = new JsonObject { ["reason"] = "Completed", ["exitCode"] = 0 } },
            _ => new JsonObject { ["terminated"] = new JsonObject { ["reason"] = "Error", ["exitCode"] = 1 } },
        };
        pod["status"] = new JsonObject
        {
            ["phase"] = phase,
            ["containerStatuses"] = new JsonArray(new JsonObject { ["name"] = container, ["ready"] = phase == "Running", ["restartCount"] = 0, ["state"] = state }),
        };
        return pod;
    }

    private static JsonObject Obj(string name, string uid, DateTimeOffset created, JsonObject? owner = null)
    {
        var meta = new JsonObject { ["name"] = name, ["namespace"] = Ns, ["uid"] = uid, ["resourceVersion"] = "1", ["creationTimestamp"] = Ts(created) };
        if (owner is not null) meta["ownerReferences"] = new JsonArray(owner);
        return new JsonObject { ["metadata"] = meta };
    }

    private static JsonObject Owner(string apiVersion, string kind, string name, string uid) =>
        new() { ["apiVersion"] = apiVersion, ["kind"] = kind, ["name"] = name, ["uid"] = uid, ["controller"] = true };

    private void Add(string plural, JsonObject obj)
    {
        if (!_objects.TryGetValue(plural, out var list)) _objects[plural] = list = [];
        list.Add(obj);
    }

    private static string Ts(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
}
