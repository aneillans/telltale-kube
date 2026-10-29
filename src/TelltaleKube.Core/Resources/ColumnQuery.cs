using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TelltaleKube.Core.Resources;

/// <summary>
/// Value semantics for list columns: understands plain numbers, kubectl-style ages (30s, 5m, 2h, 3d, 1y),
/// resource quantities (512Mi, 10Gi, 1k) and ready ratios (2/3), so sorting and comparisons behave like a human expects.
/// </summary>
public static partial class ColumnValue
{
    public static bool TryNumeric(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();

        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;

        // "in 30d" (future dates such as certificate expiry): the time until, so "expires:<7d" finds the soon ones.
        if (s.StartsWith("in ", StringComparison.OrdinalIgnoreCase)) return TryNumeric(s[3..], out value) && DurationRegex().IsMatch(s[3..].Trim());

        if (s.EndsWith('%') && double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;

        if (DurationRegex().Match(s) is { Success: true } d)
        {
            var n = double.Parse(d.Groups[1].Value, CultureInfo.InvariantCulture);
            value = n * d.Groups[2].Value switch
            {
                "s" => 1,
                "m" => 60,
                "h" => 3600,
                "d" => 86400,
                _ => 365 * 86400d,
            };
            return true;
        }

        if (QuantityRegex().IsMatch(s))
        {
            value = Quantity.ParseBytes(s);
            return true;
        }

        // "2/3" (ready/desired): order by the first number, then the second.
        if (RatioRegex().Match(s) is { Success: true } r)
        {
            value = double.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture)
                + (double.Parse(r.Groups[2].Value, CultureInfo.InvariantCulture) / 1_000_000d);
            return true;
        }

        return false;
    }

    /// <summary>Numeric-aware comparison; falls back to a natural (digit-run aware), case-insensitive string order.</summary>
    public static int Compare(string? a, string? b)
    {
        a ??= "";
        b ??= "";
        if (TryNumeric(a, out var x) && TryNumeric(b, out var y)) return x.CompareTo(y);
        // Empty values sort last in ascending order.
        if (a.Length == 0 || b.Length == 0) return (a.Length == 0).CompareTo(b.Length == 0);
        return NaturalCompare(a, b);
    }

    /// <summary>
    /// Matches one cell against a filter expression:
    /// <c>text</c> (contains), <c>=text</c> (exact), <c>!expr</c> (negate), <c>a*b</c> (wildcards),
    /// <c>&gt;5</c>, <c>&gt;=1Gi</c>, <c>&lt;2h</c>, <c>&lt;=3</c> (numeric/quantity/age comparisons).
    /// All matching is case-insensitive.
    /// </summary>
    public static bool Matches(string? value, string? expression)
    {
        value ??= "";
        var expr = expression?.Trim() ?? "";
        if (expr.Length == 0) return true;

        if (expr[0] == '!') return expr.Length == 1 || !Matches(value, expr[1..]);

        foreach (var op in (string[])[">=", "<=", ">", "<"])
        {
            if (!expr.StartsWith(op, StringComparison.Ordinal)) continue;
            var operand = expr[op.Length..].Trim();
            if (!TryNumeric(value, out var v) || !TryNumeric(operand, out var o)) return false;
            return op switch
            {
                ">=" => v >= o,
                "<=" => v <= o,
                ">" => v > o,
                _ => v < o,
            };
        }

        if (expr[0] == '=') return string.Equals(value, expr[1..].Trim(), StringComparison.OrdinalIgnoreCase);

        if (expr.Contains('*', StringComparison.Ordinal))
        {
            var pattern = "^" + Regex.Escape(expr).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
            try
            {
                return Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        return value.Contains(expr, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Canonical column key: "Cluster IP" → "clusterip", "Up-to-date" → "uptodate".</summary>
    public static string NormalizeKey(string header)
    {
        var sb = new StringBuilder(header.Length);
        foreach (var c in header)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                var si = i;
                var sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0');
                var nb = b[sj..j].TrimStart('0');
                var c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                var c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                if (c != 0) return c;
                i++;
                j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)(s|m|h|d|y)$")]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"^\d+(?:\.\d+)?(Ki|Mi|Gi|Ti|Pi|Ei|k|M|G|T|P|E|B)$")]
    private static partial Regex QuantityRegex();

    [GeneratedRegex(@"^(\d+)/(\d+)$")]
    private static partial Regex RatioRegex();
}

public sealed record FilterTerm(string? Column, string Expression);

/// <summary>
/// A list filter such as <c>web status:Running restarts:&gt;3 label:app=api !kube-system</c>.
/// Terms are AND-ed. <c>column:expr</c> targets one column (by header, case/space-insensitive); <c>label:</c>
/// matches labels; anything else is free text matched against name, namespace, every column and labels.
/// Quote terms containing spaces: <c>message:"back-off restarting"</c>.
/// </summary>
public sealed partial class FilterQuery
{
    private FilterQuery(IReadOnlyList<FilterTerm> terms) => Terms = terms;

    public IReadOnlyList<FilterTerm> Terms { get; }
    public bool IsEmpty => Terms.Count == 0;

    public static FilterQuery Parse(string? text)
    {
        var terms = new List<FilterTerm>();
        foreach (var token in Tokenize(text ?? ""))
        {
            var m = ColumnTermRegex().Match(token);
            terms.Add(m.Success
                ? new FilterTerm(ColumnValue.NormalizeKey(m.Groups[1].Value), Unquote(m.Groups[2].Value))
                : new FilterTerm(null, Unquote(token)));
        }
        return new FilterQuery(terms);
    }

    /// <summary>
    /// Evaluates the query. <paramref name="columns"/> maps normalized column keys to cell values; it must include
    /// "name" and (where relevant) "namespace". Unknown column names fall back to free-text matching of the whole term.
    /// </summary>
    public bool Matches(IReadOnlyDictionary<string, string> columns, IReadOnlyDictionary<string, string> labels)
    {
        foreach (var term in Terms)
        {
            if (!MatchesTerm(term, columns, labels)) return false;
        }
        return true;
    }

    private static bool MatchesTerm(FilterTerm term, IReadOnlyDictionary<string, string> columns, IReadOnlyDictionary<string, string> labels)
    {
        if (term.Column is { } col)
        {
            if (col is "label" or "labels" or "l") return MatchesAny(labels.Select(l => $"{l.Key}={l.Value}"), term.Expression);
            if (col == "ns" && columns.TryGetValue("namespace", out var ns)) return ColumnValue.Matches(ns, term.Expression);
            if (columns.TryGetValue(col, out var value)) return ColumnValue.Matches(value, term.Expression);
            // Not a known column (e.g. an image reference "repo:tag"): treat the original token as free text.
            return MatchesAny(AllValues(columns, labels), $"{col}:{term.Expression}");
        }
        return MatchesAny(AllValues(columns, labels), term.Expression);
    }

    /// <summary>Positive expressions need any field to match; negated ones need every field to not match.</summary>
    private static bool MatchesAny(IEnumerable<string> values, string expression)
    {
        if (expression.StartsWith('!')) return values.All(v => ColumnValue.Matches(v, expression));
        return values.Any(v => ColumnValue.Matches(v, expression));
    }

    private static IEnumerable<string> AllValues(IReadOnlyDictionary<string, string> columns, IReadOnlyDictionary<string, string> labels) =>
        columns.Values.Concat(labels.Select(l => $"{l.Key}={l.Value}"));

    private static IEnumerable<string> Tokenize(string text)
    {
        var sb = new StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') quoted = !quoted;
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (sb.Length > 0) yield return sb.ToString();
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static string Unquote(string s) => s.Replace("\"", "", StringComparison.Ordinal);

    // column:expr where column is a plain identifier (so "ghcr.io/app:1.2" stays free text).
    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9_-]*):(.*)$")]
    private static partial Regex ColumnTermRegex();
}
