namespace TelltaleKube.Core.Resources;

public enum ResourceCategory
{
    Cluster,
    Workloads,
    Network,
    Config,
    Storage,
    AccessControl,
    CustomResources,
}

public sealed record ResourceKind(
    string Kind,
    string Plural,
    string Group,
    string Version,
    bool Namespaced,
    ResourceCategory Category,
    string? DisplayName = null,
    string? ShortName = null)
{
    public string ApiVersion => string.IsNullOrEmpty(Group) ? Version : $"{Group}/{Version}";
    public string Title => DisplayName ?? Plural[..1].ToUpperInvariant() + Plural[1..];
    public string Key => string.IsNullOrEmpty(Group) ? Plural : $"{Plural}.{Group}";

    /// <summary>For custom resources: the CRD's printer columns (what <c>kubectl get</c> shows), when readable.</summary>
    public IReadOnlyList<PrinterColumn>? PrinterColumns { get; init; }

    /// <summary>REST collection path, e.g. <c>apis/apps/v1/namespaces/default/deployments</c>.</summary>
    public string CollectionPath(string? ns)
    {
        var prefix = string.IsNullOrEmpty(Group) ? $"api/{Version}" : $"apis/{Group}/{Version}";
        return Namespaced && !string.IsNullOrEmpty(ns)
            ? $"{prefix}/namespaces/{Uri.EscapeDataString(ns)}/{Plural}"
            : $"{prefix}/{Plural}";
    }

    public string ItemPath(string? ns, string name) => $"{CollectionPath(ns)}/{Uri.EscapeDataString(name)}";

    public bool IsScalable => Key is "deployments.apps" or "statefulsets.apps" or "replicasets.apps";
    public bool IsRestartable => Key is "deployments.apps" or "statefulsets.apps" or "daemonsets.apps";
    public bool IsPod => Key == "pods";
}

public static class ResourceKinds
{
    public static readonly ResourceKind Nodes = new("Node", "nodes", "", "v1", false, ResourceCategory.Cluster);
    public static readonly ResourceKind Namespaces = new("Namespace", "namespaces", "", "v1", false, ResourceCategory.Cluster);
    public static readonly ResourceKind Events = new("Event", "events", "", "v1", true, ResourceCategory.Cluster);

    public static readonly ResourceKind Pods = new("Pod", "pods", "", "v1", true, ResourceCategory.Workloads);
    public static readonly ResourceKind Deployments = new("Deployment", "deployments", "apps", "v1", true, ResourceCategory.Workloads);
    public static readonly ResourceKind StatefulSets = new("StatefulSet", "statefulsets", "apps", "v1", true, ResourceCategory.Workloads, "Stateful Sets");
    public static readonly ResourceKind DaemonSets = new("DaemonSet", "daemonsets", "apps", "v1", true, ResourceCategory.Workloads, "Daemon Sets");
    public static readonly ResourceKind ReplicaSets = new("ReplicaSet", "replicasets", "apps", "v1", true, ResourceCategory.Workloads, "Replica Sets");
    public static readonly ResourceKind Jobs = new("Job", "jobs", "batch", "v1", true, ResourceCategory.Workloads);
    public static readonly ResourceKind CronJobs = new("CronJob", "cronjobs", "batch", "v1", true, ResourceCategory.Workloads, "Cron Jobs");

    public static readonly ResourceKind Services = new("Service", "services", "", "v1", true, ResourceCategory.Network);
    public static readonly ResourceKind Ingresses = new("Ingress", "ingresses", "networking.k8s.io", "v1", true, ResourceCategory.Network);
    public static readonly ResourceKind NetworkPolicies = new("NetworkPolicy", "networkpolicies", "networking.k8s.io", "v1", true, ResourceCategory.Network, "Network Policies");
    public static readonly ResourceKind Endpoints = new("EndpointSlice", "endpointslices", "discovery.k8s.io", "v1", true, ResourceCategory.Network, "Endpoint Slices");

    public static readonly ResourceKind ConfigMaps = new("ConfigMap", "configmaps", "", "v1", true, ResourceCategory.Config, "Config Maps");
    public static readonly ResourceKind Secrets = new("Secret", "secrets", "", "v1", true, ResourceCategory.Config);
    public static readonly ResourceKind Hpas = new("HorizontalPodAutoscaler", "horizontalpodautoscalers", "autoscaling", "v2", true, ResourceCategory.Config, "Autoscalers");
    public static readonly ResourceKind PodDisruptionBudgets = new("PodDisruptionBudget", "poddisruptionbudgets", "policy", "v1", true, ResourceCategory.Config, "Disruption Budgets");
    public static readonly ResourceKind ResourceQuotas = new("ResourceQuota", "resourcequotas", "", "v1", true, ResourceCategory.Config, "Resource Quotas");

    public static readonly ResourceKind Pvcs = new("PersistentVolumeClaim", "persistentvolumeclaims", "", "v1", true, ResourceCategory.Storage, "Volume Claims");
    public static readonly ResourceKind Pvs = new("PersistentVolume", "persistentvolumes", "", "v1", false, ResourceCategory.Storage, "Volumes");
    public static readonly ResourceKind StorageClasses = new("StorageClass", "storageclasses", "storage.k8s.io", "v1", false, ResourceCategory.Storage, "Storage Classes");

    public static readonly ResourceKind ServiceAccounts = new("ServiceAccount", "serviceaccounts", "", "v1", true, ResourceCategory.AccessControl, "Service Accounts");
    public static readonly ResourceKind Roles = new("Role", "roles", "rbac.authorization.k8s.io", "v1", true, ResourceCategory.AccessControl);
    public static readonly ResourceKind RoleBindings = new("RoleBinding", "rolebindings", "rbac.authorization.k8s.io", "v1", true, ResourceCategory.AccessControl, "Role Bindings");
    public static readonly ResourceKind ClusterRoles = new("ClusterRole", "clusterroles", "rbac.authorization.k8s.io", "v1", false, ResourceCategory.AccessControl, "Cluster Roles");
    public static readonly ResourceKind ClusterRoleBindings = new("ClusterRoleBinding", "clusterrolebindings", "rbac.authorization.k8s.io", "v1", false, ResourceCategory.AccessControl, "Cluster Role Bindings");

    public static readonly ResourceKind Crds = new("CustomResourceDefinition", "customresourcedefinitions", "apiextensions.k8s.io", "v1", false, ResourceCategory.CustomResources, "Definitions");

    public static IReadOnlyList<ResourceKind> BuiltIn { get; } =
    [
        Nodes, Namespaces, Events,
        Pods, Deployments, StatefulSets, DaemonSets, ReplicaSets, Jobs, CronJobs,
        Services, Ingresses, NetworkPolicies, Endpoints,
        ConfigMaps, Secrets, Hpas, PodDisruptionBudgets, ResourceQuotas,
        Pvcs, Pvs, StorageClasses,
        ServiceAccounts, Roles, RoleBindings, ClusterRoles, ClusterRoleBindings,
        Crds,
    ];

    public static ResourceKind? FindByKind(string apiVersion, string kind) =>
        BuiltIn.FirstOrDefault(k => k.Kind == kind && k.ApiVersion == apiVersion)
        ?? BuiltIn.FirstOrDefault(k => k.Kind == kind);
}
