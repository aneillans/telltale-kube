using Avalonia;
using Avalonia.Controls;

namespace TelltaleKube.App.Controls;

/// <summary>
/// <c>ctl:GridLayout.Columns="{Binding ColumnLayout}"</c>: sets a Grid's column widths from a bindable string such as
/// <c>28,2.4*,0,*,*,64</c>, so header, filter and row grids share one per-list layout (0 = hidden column).
/// </summary>
public static class GridLayout
{
    public static readonly AttachedProperty<string?> ColumnsProperty =
        AvaloniaProperty.RegisterAttached<Grid, string?>("Columns", typeof(GridLayout));

    static GridLayout() => ColumnsProperty.Changed.AddClassHandler<Grid>((grid, e) =>
    {
        if (e.NewValue is string { Length: > 0 } layout) grid.ColumnDefinitions = new ColumnDefinitions(layout);
    });

    public static string? GetColumns(Grid grid) => grid.GetValue(ColumnsProperty);

    public static void SetColumns(Grid grid, string? value) => grid.SetValue(ColumnsProperty, value);
}
