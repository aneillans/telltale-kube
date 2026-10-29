using System.Globalization;

namespace TelltaleKube.Core.Resources;

/// <summary>Minimal Kubernetes resource.Quantity parsing for CPU (cores/millicores/nanocores) and memory.</summary>
public static class Quantity
{
    public static long ParseCpuMillis(string? q)
    {
        if (string.IsNullOrWhiteSpace(q)) return 0;
        q = q.Trim();
        return q[^1] switch
        {
            'n' => (long)(ParseNumber(q[..^1]) / 1_000_000),
            'u' => (long)(ParseNumber(q[..^1]) / 1_000),
            'm' => (long)ParseNumber(q[..^1]),
            _ => (long)(ParseNumber(q) * 1000),
        };
    }

    public static long ParseBytes(string? q)
    {
        if (string.IsNullOrWhiteSpace(q)) return 0;
        q = q.Trim();
        (string suffix, double factor)[] units =
        [
            ("Ki", 1024d), ("Mi", 1024d * 1024), ("Gi", 1024d * 1024 * 1024), ("Ti", 1024d * 1024 * 1024 * 1024), ("Pi", Math.Pow(1024, 5)), ("Ei", Math.Pow(1024, 6)),
            ("k", 1e3), ("M", 1e6), ("G", 1e9), ("T", 1e12), ("P", 1e15), ("E", 1e18), ("m", 1e-3),
        ];
        foreach (var (suffix, factor) in units)
        {
            if (q.EndsWith(suffix, StringComparison.Ordinal)) return (long)(ParseNumber(q[..^suffix.Length]) * factor);
        }
        return (long)ParseNumber(q);
    }

    public static string FormatCpu(long millis)
    {
        if (millis < 1000) return $"{millis}m";
        var cores = millis / 1000d;
        return cores.ToString(cores >= 10 ? "0" : "0.##", CultureInfo.InvariantCulture) + (cores == 1 ? " core" : " cores");
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "Ki", "Mi", "Gi", "Ti", "Pi"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return i == 0 ? $"{bytes}B" : v.ToString(v >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture) + units[i];
    }

    private static double ParseNumber(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
}
