using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleKube.App.Services;
using TelltaleKube.Core.Config;

namespace TelltaleKube.App.ViewModels;

public sealed partial class ImportContextOption : ViewModelBase
{
    public required string Name { get; init; }
    public required string Server { get; init; }
    public required string Auth { get; init; }
    public bool Warning => Auth.Contains("exec", StringComparison.Ordinal) || Auth.Contains("insecure", StringComparison.Ordinal);

    [ObservableProperty]
    public partial bool Selected { get; set; } = true;
}

public sealed partial class ImportViewModel : PageViewModel
{
    private readonly MainViewModel _main;
    private readonly AppState _state;

    public ImportViewModel(MainViewModel main, AppState state, string text)
    {
        _main = main;
        _state = state;
        ReadOnly = state.Settings.ReadOnlyByDefault;
        Text = text;
    }

    public override string Title => "Import kubeconfig";

    public ObservableCollection<ImportContextOption> Contexts { get; } = [];

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial bool ReadOnly { get; set; }

    public bool IsMobile => !Platform.Current.IsDesktop;

    partial void OnTextChanged(string value) => Parse(value);

    private void Parse(string text)
    {
        Contexts.Clear();
        Error = null;
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var cfg = KubeconfigLoader.Parse(text);
            foreach (var ctx in cfg.Contexts)
            {
                string server, auth;
                try
                {
                    var r = cfg.Resolve(ctx.Name);
                    server = r.Cluster.Server;
                    auth = DescribeAuth(r);
                }
                catch (KubeconfigException ex)
                {
                    server = "";
                    auth = ex.Message;
                }
                Contexts.Add(new ImportContextOption { Name = ctx.Name, Server = server, Auth = auth });
            }
            if (Contexts.Count == 0) Error = "No contexts found in this kubeconfig.";
        }
        catch (KubeconfigException ex)
        {
            Error = ex.Message;
        }
    }

    private string DescribeAuth(ResolvedContext r)
    {
        var parts = new List<string>();
        var u = r.User;
        if (u.Exec is not null) parts.Add(IsMobile ? $"exec plugin '{u.Exec.Command}' (not supported on mobile)" : $"exec plugin '{u.Exec.Command}'");
        else if (!string.IsNullOrEmpty(u.Token)) parts.Add("bearer token");
        else if (u.ClientCertificateData is not null) parts.Add("client certificate");
        else if (u.AuthProvider is not null) parts.Add($"auth-provider '{u.AuthProvider.Name}'");
        else if (!string.IsNullOrEmpty(u.Username)) parts.Add("basic auth (deprecated)");
        else parts.Add("no credentials");
        if (r.Cluster.InsecureSkipTlsVerify) parts.Add("insecure TLS");
        return string.Join(" · ", parts);
    }

    [RelayCommand]
    private async Task PasteFromClipboard()
    {
        if (_main.ReadClipboard is not null) Text = await _main.ReadClipboard() ?? "";
    }

    [RelayCommand]
    private async Task Import()
    {
        var selected = Contexts.Where(c => c.Selected).Select(c => c.Name).ToList();
        if (selected.Count == 0)
        {
            Error = "Select at least one context.";
            return;
        }
        try
        {
            var n = await _state.ImportAsync(Text, selected, ReadOnly);
            Text = ""; // drop the plaintext copy from memory as soon as it is encrypted
            _main.Toast($"Imported {n} cluster{(n == 1 ? "" : "s")}");
            await _main.GoBack();
            _main.Home.Refresh();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }
}
