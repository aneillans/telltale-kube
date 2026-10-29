using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TelltaleKube.App.ViewModels;

public sealed record DialogButton(string Text, string Result, bool IsDestructive = false, bool IsPrimary = false);

public sealed partial class DialogViewModel : ViewModelBase
{
    private readonly TaskCompletionSource<string> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DialogViewModel(string title, string message, params DialogButton[] buttons)
    {
        Title = title;
        Message = message;
        foreach (var b in buttons) Buttons.Add(b);
    }

    public string Title { get; }
    public string Message { get; }
    public ObservableCollection<DialogButton> Buttons { get; } = [];

    /// <summary>Monospace detail block, e.g. the exact command an exec plugin will run.</summary>
    public string? Detail { get; init; }

    /// <summary>When set, destructive buttons stay disabled until the user types this text (e.g. the resource name).</summary>
    public string? TypeToConfirm { get; init; }

    public string? InputLabel { get; init; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChooseCommand))]
    public partial string TypedText { get; set; } = "";

    [ObservableProperty]
    public partial string InputText { get; set; } = "";

    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public bool HasTypeToConfirm => !string.IsNullOrEmpty(TypeToConfirm);
    public bool HasInput => !string.IsNullOrEmpty(InputLabel);
    public string TypeToConfirmPrompt => $"Type '{TypeToConfirm}' to confirm";

    public Task<string> Result => _tcs.Task;

    private bool CanChoose(DialogButton? button) =>
        button is not null && (!button.IsDestructive || !HasTypeToConfirm || TypedText == TypeToConfirm);

    [RelayCommand(CanExecute = nameof(CanChoose))]
    private void Choose(DialogButton? button) => _tcs.TrySetResult(button?.Result ?? "cancel");

    public void Cancel() => _tcs.TrySetResult("cancel");
}

public interface IDialogService
{
    Task<string> ShowAsync(DialogViewModel dialog);
    void Toast(string message, bool isError = false);
}

public static class DialogServiceExtensions
{
    public static async Task<bool> ConfirmAsync(this IDialogService dialogs, string title, string message, string confirm, bool destructive = false, string? typeToConfirm = null)
    {
        var result = await dialogs.ShowAsync(new DialogViewModel(title, message,
            new DialogButton("Cancel", "cancel"),
            new DialogButton(confirm, "ok", destructive, !destructive))
        { TypeToConfirm = typeToConfirm });
        return result == "ok";
    }
}
