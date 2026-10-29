using Avalonia.Controls;
using TelltaleKube.App.ViewModels;

namespace TelltaleKube.App.Views;

public partial class ResourceListView : UserControl
{
    /// <summary>Below this list width the toolbar stacks and rows switch to the two-line layout.</summary>
    private const double NarrowWidth = 620;

    public ResourceListView() => InitializeComponent();

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (DataContext is ResourceListViewModel vm) vm.IsNarrow = e.NewSize.Width < NarrowWidth;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ResourceListViewModel vm && Bounds.Width > 0) vm.IsNarrow = Bounds.Width < NarrowWidth;
    }
}
