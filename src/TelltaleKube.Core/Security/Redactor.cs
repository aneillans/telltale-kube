using System.Text.RegularExpressions;

namespace TelltaleKube.Core.Security;

/// <summary>Scrubs credentials from text before it is displayed, logged or copied to the clipboard.</summary>
public static partial class Redactor
{
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = BearerRegex().Replace(text, "$1[REDACTED]");
        text = JwtRegex().Replace(text, "[REDACTED-JWT]");
        text = PemKeyRegex().Replace(text, "-----BEGIN $1PRIVATE KEY-----[REDACTED]-----END $1PRIVATE KEY-----");
        return KeyValueRegex().Replace(text, "$1$2[REDACTED]");
    }

    [GeneratedRegex(@"(Bearer\s+)[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]+")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"-----BEGIN ([A-Z ]*?)PRIVATE KEY-----[\s\S]*?-----END \1PRIVATE KEY-----")]
    private static partial Regex PemKeyRegex();

    [GeneratedRegex(@"((?:token|password|client-key-data|id-token|refresh-token)\s*[:=]\s*)(""?)[^\s""]+", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueRegex();
}
