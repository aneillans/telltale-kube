using Avalonia;
using Avalonia.Controls;

namespace TelltaleKube.App.Controls;

/// <summary>
/// Wraps cards in rows. Wide layouts use cards of <see cref="ItemWidth"/>; when fewer than two would fit (phones), it
/// switches to two equal columns that fill the width, as long as each stays at least <see cref="MinItemWidth"/>.
/// </summary>
public sealed class CardPanel : Panel
{
    public static readonly StyledProperty<double> ItemWidthProperty = AvaloniaProperty.Register<CardPanel, double>(nameof(ItemWidth), 220);
    public static readonly StyledProperty<double> MinItemWidthProperty = AvaloniaProperty.Register<CardPanel, double>(nameof(MinItemWidth), 150);
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<CardPanel, double>(nameof(Spacing), 12);

    static CardPanel() => AffectsMeasure<CardPanel>(ItemWidthProperty, MinItemWidthProperty, SpacingProperty);

    public double ItemWidth { get => GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double MinItemWidth { get => GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    public static (int Columns, double Width) Layout(double available, double itemWidth, double minItemWidth, double spacing)
    {
        if (double.IsInfinity(available)) return (int.MaxValue, itemWidth);
        var columns = (int)Math.Floor((available + spacing) / (itemWidth + spacing));
        if (columns >= 2) return (columns, itemWidth);
        columns = available >= (2 * minItemWidth) + spacing ? 2 : 1;
        return (columns, Math.Max(0, (available - (spacing * (columns - 1))) / columns));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (columns, width) = Layout(availableSize.Width, ItemWidth, MinItemWidth, Spacing);
        double height = 0, rowHeight = 0, maxRowWidth = 0;
        var visible = Children.Where(c => c.IsVisible).ToList();
        for (var i = 0; i < visible.Count; i++)
        {
            visible[i].Measure(new Size(width, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, visible[i].DesiredSize.Height);
            var column = i % columns;
            maxRowWidth = Math.Max(maxRowWidth, ((column + 1) * width) + (column * Spacing));
            if (column == columns - 1 || i == visible.Count - 1)
            {
                height += rowHeight + (i == visible.Count - 1 ? 0 : Spacing);
                rowHeight = 0;
            }
        }
        return new Size(maxRowWidth, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (columns, width) = Layout(finalSize.Width, ItemWidth, MinItemWidth, Spacing);
        var visible = Children.Where(c => c.IsVisible).ToList();
        double y = 0;
        for (var start = 0; start < visible.Count; start += columns)
        {
            var row = visible.Skip(start).Take(columns).ToList();
            var rowHeight = row.Max(c => c.DesiredSize.Height);
            for (var i = 0; i < row.Count; i++) row[i].Arrange(new Rect(i * (width + Spacing), y, width, rowHeight));
            y += rowHeight + Spacing;
        }
        return finalSize;
    }
}
