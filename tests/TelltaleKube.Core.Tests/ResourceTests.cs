using System.Text.Json.Nodes;
using TelltaleKube.Core.Resources;
using TelltaleKube.Core.Serialization;

namespace TelltaleKube.Core.Tests;

public class ResourceTests
{
    [Fact]
    public void Paths_for_core_group_and_cluster_scoped_kinds()
    {
        Assert.Equal("api/v1/namespaces/kube-system/pods", ResourceKinds.Pods.CollectionPath("kube-system"));
        Assert.Equal("api/v1/pods", ResourceKinds.Pods.CollectionPath(null));
        Assert.Equal("apis/apps/v1/namespaces/a/deployments/web", ResourceKinds.Deployments.ItemPath("a", "web"));
        Assert.Equal("api/v1/nodes", ResourceKinds.Nodes.CollectionPath("ignored"));
    }

    [Fact]
    public void Pod_status_prefers_container_reason()
    {
        var pod = new KubeObject(JsonNode.Parse("""
            {"metadata":{"name":"p"},"spec":{"containers":[{"name":"app"}]},
             "status":{"phase":"Running","containerStatuses":[{"name":"app","ready":false,"restartCount":7,"state":{"waiting":{"reason":"CrashLoopBackOff"}}}]}}
            """)!.AsObject());

        Assert.Equal("CrashLoopBackOff", ResourcePresenter.PodPhase(pod));
        Assert.Equal(Health.Error, ResourcePresenter.HealthOf(ResourceKinds.Pods, pod));
        var cols = ResourcePresenter.ColumnsFor(ResourceKinds.Pods);
        Assert.Equal("0/1", cols.Single(c => c.Header == "Ready").Value(pod));
        Assert.Equal("7", cols.Single(c => c.Header == "Restarts").Value(pod));
    }

    [Fact]
    public void Deployment_health()
    {
        static KubeObject D(int want, int ready) => new(new JsonObject
        {
            ["spec"] = new JsonObject { ["replicas"] = want },
            ["status"] = new JsonObject { ["readyReplicas"] = ready },
        });
        Assert.Equal(Health.Ok, ResourcePresenter.HealthOf(ResourceKinds.Deployments, D(3, 3)));
        Assert.Equal(Health.Warning, ResourcePresenter.HealthOf(ResourceKinds.Deployments, D(3, 1)));
        Assert.Equal(Health.Error, ResourcePresenter.HealthOf(ResourceKinds.Deployments, D(3, 0)));
    }

    [Theory]
    [InlineData(30, "30s")]
    [InlineData(600, "10m")]
    [InlineData(7200, "2h")]
    [InlineData(86400 * 3, "3d")]
    [InlineData(86400 * 800, "2y")]
    public void Ages_are_kubectl_style(int seconds, string expected) =>
        Assert.Equal(expected, ResourcePresenter.FormatAge(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData("250m", 250)]
    [InlineData("2", 2000)]
    [InlineData("1500000n", 1)]
    [InlineData("0.5", 500)]
    public void Cpu_quantities(string q, long millis) => Assert.Equal(millis, Quantity.ParseCpuMillis(q));

    [Theory]
    [InlineData("128Mi", 128L * 1024 * 1024)]
    [InlineData("1Gi", 1024L * 1024 * 1024)]
    [InlineData("1k", 1000)]
    [InlineData("1024", 1024)]
    public void Memory_quantities(string q, long bytes) => Assert.Equal(bytes, Quantity.ParseBytes(q));

    [Fact]
    public void Yaml_json_round_trip_preserves_types()
    {
        var json = JsonNode.Parse("""
            {"apiVersion":"v1","kind":"ConfigMap","metadata":{"name":"cfg","labels":{"tier":"1"}},
             "data":{"enabled":"true","port":"8080","country":"no","empty":"","script":"line1\nline2\n","version":"1.20"},
             "replicas":3,"ratio":0.5,"flag":true,"nothing":null,"list":[1,"2",{"a":"b"}]}
            """)!;

        var yaml = YamlJson.ToYaml(json);
        var back = YamlJson.FromYamlSingle(yaml);

        Assert.True(JsonNode.DeepEquals(json, back), $"Round trip changed the document:\n{yaml}\n{back.ToJsonString()}");
    }

    [Fact]
    public void Multi_document_yaml_is_split()
    {
        var docs = YamlJson.FromYaml("""
            apiVersion: v1
            kind: Namespace
            metadata: {name: a}
            ---
            apiVersion: v1
            kind: Namespace
            metadata: {name: b}
            ---
            """);
        Assert.Equal(["a", "b"], docs.Select(d => d["metadata"]!["name"]!.ToString()));
    }

    [Fact]
    public void Alias_bombs_are_rejected()
    {
        var bomb = new System.Text.StringBuilder("a: &a [\"lol\",\"lol\",\"lol\",\"lol\",\"lol\",\"lol\",\"lol\",\"lol\",\"lol\"]\n");
        for (var i = 1; i < 10; i++)
        {
            bomb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{(char)('a' + i)}: &{(char)('a' + i)} [*{(char)('a' + i - 1)},*{(char)('a' + i - 1)},*{(char)('a' + i - 1)},*{(char)('a' + i - 1)},*{(char)('a' + i - 1)},*{(char)('a' + i - 1)},*{(char)('a' + i - 1)},*{(char)('a' + i - 1)},*{(char)('a' + i - 1)}]\n");
        }
        Assert.Throws<FormatException>(() => YamlJson.FromYaml(bomb.ToString()));
    }

    [Fact]
    public void Non_mapping_documents_are_rejected()
    {
        Assert.Throws<FormatException>(() => YamlJson.FromYaml("- just\n- a list\n"));
        Assert.Throws<FormatException>(() => YamlJson.FromYaml("key: [unterminated"));
    }

    [Fact]
    public void Node_status_includes_cordon_and_pressure()
    {
        var node = new KubeObject(JsonNode.Parse("""
            {"spec":{"unschedulable":true},"status":{"conditions":[
              {"type":"Ready","status":"True"},{"type":"MemoryPressure","status":"True"},{"type":"DiskPressure","status":"False"}]}}
            """)!.AsObject());
        Assert.Equal("Ready,SchedulingDisabled,MemoryPressure", ResourcePresenter.ColumnsFor(ResourceKinds.Nodes)[0].Value(node));
        Assert.Equal(Health.Warning, ResourcePresenter.HealthOf(ResourceKinds.Nodes, node));

        var down = new KubeObject(JsonNode.Parse("""{"status":{"conditions":[{"type":"Ready","status":"Unknown"}]}}""")!.AsObject());
        Assert.Equal(Health.Error, ResourcePresenter.HealthOf(ResourceKinds.Nodes, down));
    }
}

public class CustomResourceFamilyTests
{
    [Theory]
    [InlineData("longhorn.io", "longhorn.io", "Longhorn")]
    [InlineData("helm.cattle.io", "helm.cattle.io", "Helm (k3s)")]
    [InlineData("management.cattle.io", "cattle.io", "Rancher")]
    [InlineData("source.toolkit.fluxcd.io", "fluxcd.io", "Flux")]
    [InlineData("helm.toolkit.fluxcd.io", "fluxcd.io", "Flux")]
    [InlineData("acme.cert-manager.io", "cert-manager.io", "cert-manager")]
    [InlineData("gateway.networking.k8s.io", "gateway.networking.k8s.io", "Gateway API")]
    [InlineData("infrastructure.cluster.x-k8s.io", "cluster.x-k8s.io", "Cluster API")]
    [InlineData("stable.example.com", "example.com", "example.com")]
    [InlineData("apps.mycorp.co.uk", "mycorp.co.uk", "mycorp.co.uk")]
    [InlineData("my-operator.github.io", "my-operator.github.io", "my-operator.github.io")]
    public void Related_groups_share_a_family(string group, string key, string title)
    {
        Assert.Equal(key, CustomResourceFamily.KeyOf(group));
        Assert.Equal(title, CustomResourceFamily.TitleOf(group));
    }

    [Theory]
    [InlineData("apps", true)]
    [InlineData("storage.k8s.io", true)]
    [InlineData("rbac.authorization.k8s.io", true)]
    [InlineData("flowcontrol.apiserver.k8s.io", true)]
    [InlineData("gateway.networking.k8s.io", false)]
    [InlineData("snapshot.storage.k8s.io", false)]
    [InlineData("cluster.x-k8s.io", false)]
    [InlineData("longhorn.io", false)]
    public void Built_in_groups_are_not_custom(string group, bool builtIn) => Assert.Equal(builtIn, CustomResourceFamily.IsBuiltInGroup(group));
}

public class PrinterColumnsTests
{
    private static readonly JsonNode Crds = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "crds.json")))!;

    private static KubeObject CertificateRequest() => new(JsonNode.Parse("""
        {
          "apiVersion": "cert-manager.io/v1", "kind": "CertificateRequest",
          "metadata": { "name": "web-tls-1", "namespace": "shop", "creationTimestamp": "2026-01-01T00:00:00Z" },
          "spec": { "issuerRef": { "name": "letsencrypt", "kind": "ClusterIssuer" }, "username": "system:serviceaccount:cert-manager:cert-manager" },
          "status": { "conditions": [
            { "type": "Approved", "status": "True", "message": "Certificate request has been approved by cert-manager.io" },
            { "type": "Ready", "status": "False", "reason": "Failed", "message": "Failed to wait for order resource to be ready" } ] }
        }
        """)!.AsObject());

    [Fact]
    public void Reads_printer_columns_for_the_served_version_and_skips_name_and_age()
    {
        var columns = PrinterColumns.FromCrds(Crds, new Dictionary<string, string> { ["volumes.longhorn.io"] = "v1beta2" });
        Assert.Equal(["Approved", "Denied", "Ready", "Issuer", "Requester", "Status"], columns["certificaterequests.cert-manager.io"].Select(c => c.Name));
        Assert.Equal(1, columns["certificaterequests.cert-manager.io"][^1].Priority);
        Assert.Equal(["Data Engine", "State", "Robustness", "Scheduled", "Size", "Node"], columns["volumes.longhorn.io"].Select(c => c.Name));

        // Without a discovered version, the storage version wins.
        Assert.Equal("Data Engine", PrinterColumns.FromCrds(Crds, new Dictionary<string, string>())["volumes.longhorn.io"][0].Name);
    }

    [Fact]
    public void Printer_columns_become_list_columns_and_drive_health()
    {
        var columns = PrinterColumns.FromCrds(Crds, new Dictionary<string, string>());
        var kind = new ResourceKind("CertificateRequest", "certificaterequests", "cert-manager.io", "v1", true, ResourceCategory.CustomResources)
        {
            PrinterColumns = columns["certificaterequests.cert-manager.io"],
        };
        var o = CertificateRequest();
        var cells = ResourcePresenter.ColumnsFor(kind).ToDictionary(c => c.Header, c => c.Value(o));
        Assert.Equal("True", cells["Approved"]);
        Assert.Equal("", cells["Denied"]);
        Assert.Equal("False", cells["Ready"]);
        Assert.Equal("letsencrypt", cells["Issuer"]);
        Assert.Equal("Failed to wait for order resource to be ready", cells["Status"]);
        Assert.Equal(Health.Error, ResourcePresenter.HealthOf(kind, o));

        // No printer columns readable: fall back to the Ready condition.
        var plain = kind with { PrinterColumns = null };
        Assert.Equal(["Ready", "Message"], ResourcePresenter.ColumnsFor(plain).Select(c => c.Header));
    }

    [Theory]
    [InlineData(".spec.issuerRef.name", "letsencrypt")]
    [InlineData("{.spec.issuerRef.kind}", "ClusterIssuer")]
    [InlineData(".status.conditions[0].type", "Approved")]
    [InlineData(".status.conditions[-1].type", "Ready")]
    [InlineData(".status.conditions[*].type", "Approved,Ready")]
    [InlineData(".status.conditions[?(@.type=='Ready')].reason", "Failed")]
    [InlineData(".status.conditions[?(@.type != \"Ready\")].type", "Approved")]
    [InlineData(".status.conditions[?(@.reason)].type", "Ready")]
    [InlineData(".metadata['namespace']", "shop")]
    [InlineData(".spec.missing.deeper", "")]
    [InlineData(".status.conditions[9].type", "")]
    [InlineData(".status..type", "")]
    public void Evaluates_kubectl_jsonpath(string path, string expected) =>
        Assert.Equal(expected, PrinterColumns.Format(new PrinterColumn("X", "string", path), CertificateRequest()));

    [Fact]
    public void Date_columns_render_as_ages_past_and_future()
    {
        var o = new KubeObject(JsonNode.Parse($$"""{ "status": { "notAfter": "{{DateTimeOffset.UtcNow.AddDays(30).AddHours(1):O}}", "renewed": "{{DateTimeOffset.UtcNow.AddDays(-2).AddMinutes(-1):O}}" } }""")!.AsObject());
        Assert.Equal("in 30d", PrinterColumns.Format(new PrinterColumn("Expires", "date", ".status.notAfter"), o));
        Assert.Equal("2d", PrinterColumns.Format(new PrinterColumn("Renewed", "date", ".status.renewed"), o));
    }
}
