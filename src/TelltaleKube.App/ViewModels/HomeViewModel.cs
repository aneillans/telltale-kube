using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleKube.App.Services;
using TelltaleKube.Core.Config;

namespace TelltaleKube.App.ViewModels;

public sealed class ClusterCardViewModel(ClusterEntry entry)
{
    public ClusterEntry Entry { get; } = entry;
    public string Name => Entry.DisplayName;
    public string Server => Entry.Server;
    public string Initials => string.Concat(Entry.DisplayName.Split(['-', '_', ' ', '.', '@'], StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));
    public bool IsFromSystem => Entry.Source == ClusterSource.SystemKubeconfig;
    public bool IsReadOnly => Entry.IsReadOnly;
    public bool UsesExec => SafeCheck(() => Entry.UsesExecPlugin);
    public bool IsInsecure => SafeCheck(() => Entry.InsecureTls);
    public string SourceLabel => IsFromSystem ? "kubeconfig" : "saved";

    private static bool SafeCheck(Func<bool> f)
    {
        try
        {
            return f();
        }
        catch (KubeconfigException)
        {
            return false;
        }
    }
}

public sealed partial class HomeViewModel : PageViewModel
{
    private readonly MainViewModel _main;
    private readonly AppState _state;

    public HomeViewModel(MainViewModel main, AppState state)
    {
        _main = main;
        _state = state;
    }

    public override string Title => "Clusters";

    public ObservableCollection<ClusterCardViewModel> Clusters { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredClusters))]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    public partial string? Banner { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    public IEnumerable<ClusterCardViewModel> FilteredClusters =>
        string.IsNullOrWhiteSpace(Filter)
            ? Clusters
            : Clusters.Where(c => c.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase) || c.Server.Contains(Filter, StringComparison.OrdinalIgnoreCase));

    public bool IsDesktop => Platform.Current.IsDesktop;

    [RelayCommand]
    public void Refresh()
    {
        Clusters.Clear();
        foreach (var entry in _state.GetClusters()) Clusters.Add(new ClusterCardViewModel(entry));
        IsEmpty = Clusters.Count == 0;
        Banner = _state.LoadError ?? (_state.Vault.Protector.IsStrong
            ? null
            : "No OS secret store was found, so saved credentials are protected by file permissions only. Install a Secret Service provider (e.g. GNOME Keyring or KWallet) for full protection.");
        OnPropertyChanged(nameof(FilteredClusters));
    }

    [RelayCommand]
    private async Task Connect(ClusterCardViewModel? card)
    {
        if (card is null) return;
        try
        {
            var cluster = new ClusterViewModel(_main, card.Entry);
            _main.Navigate(cluster);
            await cluster.ConnectAsync();
        }
        catch (Exception ex)
        {
            _main.Toast(TelltaleKube.Core.Security.Redactor.Redact(ex.Message), isError: true);
        }
    }

    [RelayCommand]
    private async Task Remove(ClusterCardViewModel? card)
    {
        if (card is null || card.IsFromSystem) return;
        if (!await _main.ConfirmAsync("Remove cluster", $"Remove '{card.Name}' and its stored credentials from this device?", "Remove", destructive: true)) return;
        await _state.RemoveAsync(card.Entry.Id);
        Refresh();
    }

    [RelayCommand]
    private async Task ImportFile()
    {
        if (_main.PickTextFile is null) return;
        try
        {
            var text = await _main.PickTextFile();
            if (text is not null) OpenImport(text);
        }
        catch (Exception ex)
        {
            _main.Toast(ex.Message, isError: true);
        }
    }

    [RelayCommand]
    private void ImportPaste() => _main.Navigate(new ImportViewModel(_main, _state, ""));

    private void OpenImport(string text) => _main.Navigate(new ImportViewModel(_main, _state, text));
}
