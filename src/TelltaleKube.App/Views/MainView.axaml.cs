using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using TelltaleKube.App.ViewModels;
using TelltaleKube.Core.Config;

namespace TelltaleKube.App.Views;

public partial class MainView : UserControl
{
    /// <summary>Below this width the UI switches to the single-pane phone layout.</summary>
    private const double CompactWidth = 760;

    public MainView() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Vm is not { } vm || TopLevel.GetTopLevel(this) is not { } top) return;

        vm.PickTextFile = async () =>
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select a kubeconfig",
                AllowMultiple = false,
            });
            if (files.Count == 0) return null;
            await using var stream = await files[0].OpenReadAsync();
            if (stream.CanSeek && stream.Length > KubeconfigLoader.MaxFileBytes) throw new KubeconfigException("File is too large to be a kubeconfig.");
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        };
        vm.CopyToClipboard = async text =>
        {
            if (top.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        };
        vm.ClearClipboardIf = async expected =>
        {
            if (top.Clipboard is { } clipboard && await clipboard.TryGetTextAsync() == expected) await clipboard.ClearAsync();
        };
        vm.ReadClipboard = async () => top.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
        vm.OpenUrl = async uri => await top.Launcher.LaunchUriAsync(uri);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (Vm is { } vm) vm.IsCompact = e.NewSize.Width < CompactWidth;
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => Vm?.Dialog?.Cancel();

    private void OnDialogPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;
}
