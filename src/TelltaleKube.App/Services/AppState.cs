using TelltaleKube.Core.Config;
using TelltaleKube.Core.Security;

namespace TelltaleKube.App.Services;

/// <summary>A connectable context: either stored in the encrypted vault or read live from the system kubeconfig.</summary>
public sealed record ClusterEntry(string Id, string DisplayName, string ContextName, string Server, ClusterSource Source, StoredCluster? Stored, Kubeconfig Config)
{
    public bool IsReadOnly => Stored?.ReadOnly ?? false;
    public bool AllowInsecureTls => Stored?.AllowInsecureTls ?? false;
    public string? DefaultNamespace => Stored?.DefaultNamespace ?? Config.FindContext(ContextName)?.Namespace;
    public bool UsesExecPlugin => Config.Resolve(ContextName).User.Exec is not null;
    public bool InsecureTls => Config.Resolve(ContextName).Cluster.InsecureSkipTlsVerify;
}

public enum ClusterSource
{
    Vault,
    SystemKubeconfig,
}

public sealed class AppState
{
    private readonly SecretVault _vault;

    public AppState(SecretVault vault) => _vault = vault;

    public SecretVault Vault => _vault;
    public VaultData Data { get; private set; } = new();
    public AppSettings Settings => Data.Settings;
    public string? LoadError { get; private set; }

    public async Task InitialiseAsync()
    {
        try
        {
            Data = await _vault.LoadAsync();
        }
        catch (Exception ex)
        {
            // Never silently overwrite an unreadable vault: surface it and keep running with an empty in-memory set.
            LoadError = $"The secure store could not be opened ({ex.Message}). Your saved clusters are unavailable until this is resolved.";
            Data = new VaultData();
        }
    }

    public Task SaveAsync() => LoadError is null ? _vault.SaveAsync() : Task.CompletedTask;

    public IReadOnlyList<ClusterEntry> GetClusters()
    {
        var list = new List<ClusterEntry>();
        foreach (var stored in Data.Clusters)
        {
            try
            {
                var cfg = KubeconfigLoader.Parse(stored.KubeconfigYaml);
                var r = cfg.Resolve(stored.ContextName);
                list.Add(new ClusterEntry(stored.Id, stored.DisplayName, stored.ContextName, r.Cluster.Server, ClusterSource.Vault, stored, cfg));
            }
            catch (KubeconfigException)
            {
                // Corrupt entry: skip but keep it in the vault so the user can remove it.
            }
        }

        if (Platform.Current.IsDesktop && Settings.UseSystemKubeconfig)
        {
            try
            {
                var system = KubeconfigLoader.LoadDefault();
                foreach (var ctx in system.Contexts)
                {
                    try
                    {
                        var r = system.Resolve(ctx.Name);
                        list.Add(new ClusterEntry("kubeconfig:" + ctx.Name, ctx.Name, ctx.Name, r.Cluster.Server, ClusterSource.SystemKubeconfig, null, system));
                    }
                    catch (KubeconfigException)
                    {
                    }
                }
            }
            catch (KubeconfigException)
            {
            }
        }
        return list;
    }

    public async Task<int> ImportAsync(string yaml, IEnumerable<string> contextNames, bool readOnly)
    {
        var cfg = KubeconfigLoader.Parse(yaml);
        var count = 0;
        foreach (var name in contextNames)
        {
            var single = KubeconfigWriter.Extract(cfg, name);
            Data.Clusters.Add(new StoredCluster
            {
                DisplayName = UniqueName(name),
                ContextName = name,
                KubeconfigYaml = KubeconfigWriter.Write(single),
                ReadOnly = readOnly,
                DefaultNamespace = single.Contexts[0].Namespace,
            });
            count++;
        }
        await SaveAsync();
        return count;
    }

    public async Task RemoveAsync(string id)
    {
        Data.Clusters.RemoveAll(c => c.Id == id);
        await SaveAsync();
    }

    private string UniqueName(string name)
    {
        var candidate = name;
        for (var i = 2; Data.Clusters.Any(c => c.DisplayName == candidate); i++) candidate = $"{name} ({i})";
        return candidate;
    }
}
