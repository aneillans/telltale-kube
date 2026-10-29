using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TelltaleKube.App.Controls;

/// <summary>A minimal line + area chart for 0–100 percentages. No charting dependency.</summary>
public sealed class Sparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke), Brushes.Teal);

    static Sparkline() => AffectsRender<Sparkline>(ValuesProperty, StrokeProperty);

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var values = Values;
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (values is null || values.Count < 2 || w <= 0 || h <= 0) return;

        var step = w / (values.Count - 1);
        Point P(int i) => new(i * step, h - (Math.Clamp(values[i], 0, 100) / 100 * (h - 2)) - 1);

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(P(0), false);
            for (var i = 1; i < values.Count; i++) g.LineTo(P(i));
            g.EndFigure(false);
        }
        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(0, h), true);
            for (var i = 0; i < values.Count; i++) g.LineTo(P(i));
            g.LineTo(new Point(w, h));
            g.EndFigure(true);
        }

        var stroke = Stroke ?? Brushes.Teal;
        var fill = stroke is ISolidColorBrush s ? new SolidColorBrush(s.Color, 0.15) : null;
        if (fill is not null) context.DrawGeometry(fill, null, area);
        context.DrawGeometry(null, new Pen(stroke, 1.5, lineJoin: PenLineJoin.Round), line);
    }
}
