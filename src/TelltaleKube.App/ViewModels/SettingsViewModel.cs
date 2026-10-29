using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleKube.App.Services;
using TelltaleKube.Core.Security;

namespace TelltaleKube.App.ViewModels;

public sealed partial class StoredClusterSettings(StoredCluster stored) : ViewModelBase
{
    public StoredCluster Stored { get; } = stored;
    public string Name => Stored.DisplayName;

    public bool ReadOnly
    {
        get => Stored.ReadOnly;
        set
        {
            Stored.ReadOnly = value;
            OnPropertyChanged();
        }
    }

    public bool AllowInsecureTls
    {
        get => Stored.AllowInsecureTls;
        set
        {
            Stored.AllowInsecureTls = value;
            OnPropertyChanged();
        }
    }
}

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly MainViewModel _main;
    private readonly AppState _state;

    public SettingsViewModel(MainViewModel main, AppState state)
    {
        _main = main;
        _state = state;
        foreach (var c in state.Data.Clusters) Clusters.Add(new StoredClusterSettings(c));
        Theme = state.Settings.Theme;
        AutoLockMinutes = state.Settings.AutoLockMinutes;
        ReadOnlyByDefault = state.Settings.ReadOnlyByDefault;
        UseSystemKubeconfig = state.Settings.UseSystemKubeconfig;
        AllowBasicAuth = state.Settings.AllowBasicAuth;
        CheckForUpdates = state.Settings.CheckForUpdates;
    }

    public override string Title => "Settings";
    public ObservableCollection<StoredClusterSettings> Clusters { get; } = [];
    public IReadOnlyList<string> Themes { get; } = ["System", "Light", "Dark"];
    public IReadOnlyList<int> LockOptions { get; } = [0, 1, 5, 15, 60];
    public bool IsDesktop => Platform.Current.IsDesktop;
    public string KeyStorage => _state.Vault.Protector.Description;
    public bool KeyStorageIsWeak => !_state.Vault.Protector.IsStrong;
    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "dev";
    public string PlatformName => Platform.Current.PlatformName;
    public int ApprovedPluginCount => _state.Data.ApprovedExecPlugins.Count;

    [ObservableProperty]
    public partial string Theme { get; set; }

    [ObservableProperty]
    public partial int AutoLockMinutes { get; set; }

    [ObservableProperty]
    public partial bool ReadOnlyByDefault { get; set; }

    [ObservableProperty]
    public partial bool UseSystemKubeconfig { get; set; }

    [ObservableProperty]
    public partial bool AllowBasicAuth { get; set; }

    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; }

    [ObservableProperty]
    public partial string? UpdateStatus { get; set; }

    partial void OnThemeChanged(string value) => App.ApplyTheme(value);

    [RelayCommand]
    private async Task Save()
    {
        var s = _state.Settings;
        s.Theme = Theme;
        s.AutoLockMinutes = AutoLockMinutes;
        s.ReadOnlyByDefault = ReadOnlyByDefault;
        s.UseSystemKubeconfig = UseSystemKubeconfig;
        s.AllowBasicAuth = AllowBasicAuth;
        s.CheckForUpdates = CheckForUpdates;
        await _state.SaveAsync();
        _main.Home.Refresh();
        _main.Toast("Settings saved");
        await _main.GoBack();
    }

    [RelayCommand]
    private async Task ForgetPluginApprovals()
    {
        _state.Data.ApprovedExecPlugins.Clear();
        await _state.SaveAsync();
        OnPropertyChanged(nameof(ApprovedPluginCount));
        _main.Toast("Credential plugin approvals cleared");
    }

    [RelayCommand]
    private async Task CheckUpdates()
    {
        UpdateStatus = "Checking…";
        UpdateStatus = await Platform.Current.CheckForUpdatesAsync(apply: false) ?? "Updates are delivered by your app store.";
    }

    [RelayCommand]
    private async Task WipeAll()
    {
        if (!await _main.ConfirmAsync("Erase all data", "Permanently delete every saved cluster, credential and setting from this device? The encryption key is destroyed, so this cannot be undone.", "Erase everything", destructive: true, typeToConfirm: "erase"))
        {
            return;
        }
        await _state.Vault.WipeAsync();
        await _state.InitialiseAsync();
        Clusters.Clear();
        _main.Home.Refresh();
        _main.Toast("All data erased");
        await _main.GoHome();
    }
}
