using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleKube.App.Services;

namespace TelltaleKube.App.ViewModels;

public sealed record ToastMessage(string Text, bool IsError);

/// <summary>Root view model: navigation stack, modal dialogs, toasts and the privacy/auto-lock screen.</summary>
public sealed partial class MainViewModel : ViewModelBase, IDialogService
{
    private readonly Stack<PageViewModel> _back = new();
    private DateTimeOffset? _backgroundedAt;

    public MainViewModel(AppState state)
    {
        State = state;
        Home = new HomeViewModel(this, state);
        CurrentPage = Home;
        ExecApprover = new ExecApprover(state, this);
    }

    public AppState State { get; }
    public HomeViewModel Home { get; }
    public ExecApprover ExecApprover { get; }
    public ObservableCollection<ToastMessage> Toasts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    public partial PageViewModel CurrentPage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialog))]
    public partial DialogViewModel? Dialog { get; set; }

    /// <summary>Narrow layout (phones, split-screen tablets, small windows).</summary>
    [ObservableProperty]
    public partial bool IsCompact { get; set; }

    /// <summary>Content hidden: app is in the app switcher or locked after inactivity.</summary>
    [ObservableProperty]
    public partial bool IsPrivacyScreen { get; set; }

    [ObservableProperty]
    public partial bool IsLocked { get; set; }

    public bool HasDialog => Dialog is not null;

    /// <summary>Wired by the view (needs a TopLevel): opens a file picker and returns the text of the chosen file.</summary>
    public Func<Task<string?>>? PickTextFile { get; set; }

    public Func<string, Task>? CopyToClipboard { get; set; }

    public Func<Task<string?>>? ReadClipboard { get; set; }

    public Func<Uri, Task>? OpenUrl { get; set; }

    public Func<string, Task>? ClearClipboardIf { get; set; }

    public async Task CopyAsync(string text, string what)
    {
        if (CopyToClipboard is null) return;
        await CopyToClipboard(text);
        Toast($"{what} copied");
    }

    /// <summary>Copies a secret value and wipes it from the clipboard after 45 seconds (unless the user copied something else).</summary>
    public async Task CopySecretAsync(string text, string what)
    {
        if (CopyToClipboard is null) return;
        await CopyToClipboard(text);
        Toast($"{what} copied. Clipboard clears in 45s.");
        DispatcherTimer.RunOnce(() => _ = ClearClipboardIf?.Invoke(text), TimeSpan.FromSeconds(45));
    }

    public bool CanGoBack => _back.Count > 0;

    public void Navigate(PageViewModel page)
    {
        _back.Push(CurrentPage);
        CurrentPage = page;
    }

    [RelayCommand]
    public async Task GoBack()
    {
        if (_back.Count == 0) return;
        var leaving = CurrentPage;
        CurrentPage = _back.Pop();
        await leaving.DisposeAsync();
    }

    public async Task GoHome()
    {
        while (_back.Count > 0) await GoBack();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        if (CurrentPage is SettingsViewModel) return;
        Navigate(new SettingsViewModel(this, State));
    }

    public async Task<string> ShowAsync(DialogViewModel dialog)
    {
        // Dialogs are queued: wait for any open one to close first.
        while (Dialog is { } open) await open.Result;
        Dialog = dialog;
        try
        {
            return await dialog.Result;
        }
        finally
        {
            Dialog = null;
        }
    }

    public void Toast(string message, bool isError = false)
    {
        var toast = new ToastMessage(message, isError);
        Dispatcher.UIThread.Post(() => Toasts.Add(toast));
        DispatcherTimer.RunOnce(() => Toasts.Remove(toast), TimeSpan.FromSeconds(isError ? 6 : 3));
    }

    public void OnBackgrounded()
    {
        _backgroundedAt = DateTimeOffset.UtcNow;
        IsPrivacyScreen = true;
    }

    public void OnForegrounded()
    {
        var minutes = State.Settings.AutoLockMinutes;
        if (_backgroundedAt is { } at && minutes > 0 && DateTimeOffset.UtcNow - at > TimeSpan.FromMinutes(minutes))
        {
            IsLocked = true;
            _ = UnlockAsync();
        }
        else if (!IsLocked)
        {
            IsPrivacyScreen = false;
        }
        _backgroundedAt = null;
    }

    [RelayCommand]
    private async Task UnlockAsync()
    {
        if (await Platform.Current.AuthenticateAsync("Unlock Telltale Kube"))
        {
            IsLocked = false;
            IsPrivacyScreen = false;
        }
    }
}
