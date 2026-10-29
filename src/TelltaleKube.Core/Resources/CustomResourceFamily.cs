namespace TelltaleKube.Core.Resources;

/// <summary>
/// Groups custom resource API groups into the product that installs them (<c>longhorn.io</c>, <c>helm.cattle.io</c>,
/// <c>*.toolkit.fluxcd.io</c>, …) so the sidebar can show one collapsible entry per product.
/// </summary>
public static class CustomResourceFamily
{
    // Well-known products. Keys are an API group or a domain suffix; the longest match wins.
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        ["longhorn.io"] = "Longhorn",
        ["helm.cattle.io"] = "Helm (k3s)",
        ["k3s.cattle.io"] = "k3s",
        ["cattle.io"] = "Rancher",
        ["fluxcd.io"] = "Flux",
        ["argoproj.io"] = "Argo",
        ["cert-manager.io"] = "cert-manager",
        ["monitoring.coreos.com"] = "Prometheus Operator",
        ["coreos.com"] = "CoreOS",
        ["traefik.io"] = "Traefik",
        ["traefik.containo.us"] = "Traefik",
        ["istio.io"] = "Istio",
        ["cilium.io"] = "Cilium",
        ["projectcalico.org"] = "Calico",
        ["tigera.io"] = "Calico",
        ["metallb.io"] = "MetalLB",
        ["kyverno.io"] = "Kyverno",
        ["velero.io"] = "Velero",
        ["keda.sh"] = "KEDA",
        ["external-secrets.io"] = "External Secrets",
        ["bitnami.com"] = "Sealed Secrets",
        ["cnpg.io"] = "CloudNativePG",
        ["min.io"] = "MinIO",
        ["grafana.com"] = "Grafana",
        ["knative.dev"] = "Knative",
        ["crossplane.io"] = "Crossplane",
        ["tekton.dev"] = "Tekton",
        ["linkerd.io"] = "Linkerd",
        ["kubevirt.io"] = "KubeVirt",
        ["rook.io"] = "Rook",
        ["ceph.rook.io"] = "Rook Ceph",
        ["nvidia.com"] = "NVIDIA",
        ["aquasecurity.github.io"] = "Trivy",
        ["gateway.networking.k8s.io"] = "Gateway API",
        ["snapshot.storage.k8s.io"] = "Volume Snapshots",
        ["cluster.x-k8s.io"] = "Cluster API",
    };

    // Public suffixes where the "domain" is the last three labels, not two.
    private static readonly HashSet<string> TwoLabelSuffixes = new(StringComparer.Ordinal)
    {
        "co.uk", "org.uk", "com.au", "co.jp", "com.br", "co.nz", "github.io", "gitlab.io", "x-k8s.io", "k8s.io",
    };

    /// <summary>Stable key that related API groups share, e.g. <c>longhorn.io</c> or <c>fluxcd.io</c>.</summary>
    public static string KeyOf(string apiGroup)
    {
        if (KnownMatch(apiGroup) is { } known) return known;
        var labels = apiGroup.Split('.');
        if (labels.Length <= 2) return apiGroup;
        var take = TwoLabelSuffixes.Contains($"{labels[^2]}.{labels[^1]}") ? 3 : 2;
        return string.Join('.', labels[^Math.Min(take, labels.Length)..]);
    }

    /// <summary>Display name for a family key: a product name when known, otherwise the domain itself.</summary>
    public static string TitleOf(string apiGroup) => KnownMatch(apiGroup) is { } known ? Known[known] : KeyOf(apiGroup);

    private static string? KnownMatch(string apiGroup)
    {
        string? best = null;
        foreach (var key in Known.Keys)
        {
            if ((apiGroup == key || apiGroup.EndsWith("." + key, StringComparison.Ordinal)) && (best is null || key.Length > best.Length)) best = key;
        }
        return best;
    }

    /// <summary>Kubernetes' own API groups (never custom resources), e.g. <c>apps</c>, <c>storage.k8s.io</c>.</summary>
    public static bool IsBuiltInGroup(string apiGroup)
    {
        if (!apiGroup.Contains('.', StringComparison.Ordinal)) return true; // apps, batch, autoscaling, policy, …
        if (!apiGroup.EndsWith(".k8s.io", StringComparison.Ordinal)) return false;
        // Built-ins are <name>.k8s.io or <name>.authorization|apiserver.k8s.io; CRDs such as
        // gateway.networking.k8s.io and snapshot.storage.k8s.io live one level deeper.
        var labels = apiGroup.Split('.');
        return labels.Length <= 3 || labels[^3] is "authorization" or "apiserver";
    }
}
