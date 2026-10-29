using Avalonia.Threading;
using TelltaleKube.App.ViewModels;
using TelltaleKube.Core.Config;
using TelltaleKube.Core.Http;

namespace TelltaleKube.App.Services;

/// <summary>
/// Kubeconfig exec plugins run arbitrary local programs, so a kubeconfig from an untrusted source could be
/// weaponised. Every distinct command line must be approved by the user; "always" approvals are fingerprinted
/// and stored in the encrypted vault.
/// </summary>
public sealed class ExecApprover(AppState state, IDialogService dialogs) : IExecApprover
{
    private readonly HashSet<string> _sessionApprovals = new(StringComparer.Ordinal);

    public async ValueTask<bool> ApproveAsync(ExecConfig exec, string fingerprint, CancellationToken ct)
    {
        if (_sessionApprovals.Contains(fingerprint) || state.Data.ApprovedExecPlugins.Contains(fingerprint)) return true;

        var detail = string.Join(' ', new[] { exec.Command }.Concat(exec.Args.Select(Quote)));
        if (exec.Env.Count > 0) detail += "\n\nEnvironment:\n" + string.Join('\n', exec.Env.Select(kv => $"  {kv.Key}={kv.Value}"));

        var result = await Dispatcher.UIThread.InvokeAsync(() => dialogs.ShowAsync(new DialogViewModel(
            "Run credential plugin?",
            "This cluster's kubeconfig wants to run a program on your computer to obtain credentials. Only allow this if you trust where the kubeconfig came from.",
            new DialogButton("Deny", "deny"),
            new DialogButton("Allow once", "once"),
            new DialogButton("Always allow", "always", IsPrimary: true))
        {
            Detail = detail,
        }));

        switch (result)
        {
            case "once":
                _sessionApprovals.Add(fingerprint);
                return true;
            case "always":
                state.Data.ApprovedExecPlugins.Add(fingerprint);
                await state.SaveAsync();
                return true;
            default:
                return false;
        }
    }

    private static string Quote(string arg) => arg.Any(char.IsWhiteSpace) || arg.Length == 0 ? $"\"{arg}\"" : arg;
}
