using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using TelltaleKube.Core.Resources;

namespace TelltaleKube.App.Converters;

public sealed class HealthToBrushConverter : IValueConverter
{
    public static readonly HealthToBrushConverter Instance = new();

    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#2EAD6B"));
    private static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#E0A100"));
    private static readonly IBrush Error = new SolidColorBrush(Color.Parse("#E5484D"));
    private static readonly IBrush Neutral = new SolidColorBrush(Color.Parse("#8A94A6"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Health.Ok => Ok,
        Health.Warning => Warning,
        Health.Error => Error,
        _ => Neutral,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Utilisation percentage → green / amber (≥ 75%) / red (≥ 90%).</summary>
public sealed class PercentToBrushConverter : IValueConverter
{
    public static readonly PercentToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        HealthToBrushConverter.Instance.Convert(value is double d ? d >= 90 ? Health.Error : d >= 75 ? Health.Warning : Health.Ok : Health.Neutral, targetType, parameter, culture);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public static class Bool
{
    public static readonly IValueConverter Not = new FuncValueConverter<bool, bool>(b => !b);
}
