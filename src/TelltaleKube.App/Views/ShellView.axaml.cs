using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using TelltaleKube.App.ViewModels;

namespace TelltaleKube.App.Views;

public partial class ShellView : UserControl
{
    public ShellView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ShellViewModel vm)
            {
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ShellViewModel.Output)) Dispatcher.UIThread.Post(OutputScroll.ScrollToEnd, DispatcherPriority.Background);
                };
            }
        };
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ShellViewModel vm) return;
        switch (e.Key)
        {
            case Key.Enter:
                vm.SendCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
                vm.HistoryUpCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Down:
                vm.HistoryDownCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
