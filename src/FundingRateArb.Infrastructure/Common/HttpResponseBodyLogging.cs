using System.Text.RegularExpressions;

namespace FundingRateArb.Infrastructure.Common;

/// <summary>
/// Helpers for safely logging HTTP response bodies in warning/error paths.
/// Truncates oversized payloads and redacts sensitive header values and JSON field
/// values (CG-API-KEY, Authorization, Proxy-Authorization, X-API-Key, X-Auth-Token,
/// apiKey/api_key/token/secret JSON fields, and naked Bearer tokens) that may have
/// been echoed back in error responses or exception messages.
/// </summary>
internal static class HttpResponseBodyLogging
{
    // Redact known credential/token key names whether they appear as HTTP headers
    // ("CG-API-KEY: secret"), JSON fields ("\"apiKey\":\"secret\""), or form-encoded
    // values ("api_key=secret"). The alternation covers:
    //   - Header names: CG-API-KEY, Authorization, Proxy-Authorization, X-API-Key,
    //     X-Auth-Token.
    //   - JSON/form fields (optional surrounding quotes): apiKey / api_key / api-key,
    //     token, secret.
    // Separator accepts ':' or '=' (headers use colon, JSON uses colon, form uses equals).
    // The value is captured up to the next structural delimiter (", \r, \n, comma,
    // closing brace) or optional closing quote. Keeps content AFTER the boundary so
    // the rest of the body stays in the log.
    // Case-insensitive + compiled + timeout bounded to guard against pathological input.
    private static readonly Regex CredentialPattern = new(
        @"(CG-API-KEY|Authorization|Proxy-Authorization|X-API-Key|X-Auth-Token|""?api[_-]?key""?|""?token""?|""?secret""?)\s*[:=]\s*""?[^""\r\n,}]*""?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    // Separate pattern for naked "Bearer <token>" forms that appear without an
    // enclosing header name (e.g., inside a free-form error message like
    // "unauthorized Bearer abc123"). Handles URL-safe Base64 / JWT characters.
    private static readonly Regex BearerTokenPattern = new(
        @"Bearer\s+[A-Za-z0-9._~+/=-]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// Sanitizes an HTTP response body (or exception message) for safe logging.
    /// Redacts any echoed credential/token values (see class-level remarks) and
    /// truncates the result to at most <paramref name="maxChars"/> characters,
    /// appending <c>"..."</c> when truncation occurs. Null or empty inputs return
    /// <see cref="string.Empty"/>. The truncation boundary is aware of UTF-16
    /// surrogate pairs and will back off one char to avoid splitting a pair.
    /// </summary>
    public static string TruncateAndSanitize(string? body, int maxChars = 500)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        var sanitized = CredentialPattern.Replace(body, "$1: REDACTED");
        sanitized = BearerTokenPattern.Replace(sanitized, "Bearer REDACTED");

        if (sanitized.Length <= maxChars)
        {
            return sanitized;
        }

        // Back off one char if the slice boundary lands on a high surrogate so we
        // don't emit a lone surrogate (invalid UTF-16 that crashes some log sinks).
        var sliceLen = maxChars;
        if (char.IsHighSurrogate(sanitized[sliceLen - 1]))
        {
            sliceLen -= 1;
        }

        return string.Concat(sanitized.AsSpan(0, sliceLen), "...");
    }
}
