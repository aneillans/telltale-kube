using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using TelltaleKube.App.ViewModels;

namespace TelltaleKube.App.Views;

public partial class LogsView : UserControl
{
    private LogsViewModel? _vm;

    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.Lines.CollectionChanged -= OnLinesChanged;
            _vm = DataContext as LogsViewModel;
            if (_vm is not null) _vm.Lines.CollectionChanged += OnLinesChanged;
        };
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_vm is not { Follow: true } || e.Action != NotifyCollectionChangedAction.Add || _vm.Lines.Count == 0) return;
        Dispatcher.UIThread.Post(() => LinesList.ScrollIntoView(_vm.Lines.Count - 1), DispatcherPriority.Background);
    }
}
