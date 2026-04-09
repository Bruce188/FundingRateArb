using System.Text.RegularExpressions;

namespace FundingRateArb.Infrastructure.Common;

/// <summary>
/// Helpers for safely logging HTTP response bodies in warning/error paths.
/// Truncates oversized payloads and redacts sensitive header values (CG-API-KEY,
/// Authorization) that may have been echoed back in error responses or exception messages.
/// </summary>
internal static class HttpResponseBodyLogging
{
    // Match the header name ("CG-API-KEY" or "Authorization"), its colon, and the entire
    // value up to the next newline/end — this covers both raw API keys and
    // "<scheme> <token>" forms (e.g., "Authorization: Bearer <jwt>"). Case-insensitive,
    // with a safety timeout to bound regex cost on hostile input.
    private static readonly Regex HeaderRedactionPattern = new(
        @"(CG-API-KEY|Authorization)\s*:\s*[^\r\n]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// Sanitizes an HTTP response body (or exception message) for safe logging.
    /// Redacts any echoed <c>CG-API-KEY</c> or <c>Authorization</c> header values and
    /// truncates the result to at most <paramref name="maxChars"/> characters, appending
    /// <c>"..."</c> when truncation occurs. Null or empty inputs return <see cref="string.Empty"/>.
    /// </summary>
    public static string TruncateAndSanitize(string? body, int maxChars = 500)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        var sanitized = HeaderRedactionPattern.Replace(body, "$1: REDACTED");

        if (sanitized.Length <= maxChars)
        {
            return sanitized;
        }

        return string.Concat(sanitized.AsSpan(0, maxChars), "...");
    }
}
