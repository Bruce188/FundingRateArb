using Serilog.Core;
using Serilog.Events;

namespace FundingRateArb.Infrastructure.Logging;

/// <summary>
/// Serilog enricher that masks structured log properties whose names end with
/// sensitive suffixes (ApiKey, SecretKey, PrivateKey, Secret, Password, etc.).
/// Uses suffix matching to avoid false positives on names like EntityKey, CacheKey, ConnectionCount.
/// Only replaces structured property values — does not alter message templates.
/// </summary>
public class SensitiveDataMaskingEnricher : ILogEventEnricher
{
    private static readonly string[] SensitivePatterns =
    {
        "ApiKey", "SecretKey", "PrivateKey",
        "Secret", "Password", "ConnectionString",
        "AccessToken", "BearerToken", "SessionToken",
        "RefreshToken", "Credential"
    };

    private static readonly ScalarValue Redacted = new("***REDACTED***");

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var propertiesToMask = new List<string>();

        foreach (var property in logEvent.Properties)
        {
            foreach (var pattern in SensitivePatterns)
            {
                if (property.Key.EndsWith(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    propertiesToMask.Add(property.Key);
                    break;
                }
            }
        }

        foreach (var name in propertiesToMask)
        {
            logEvent.AddOrUpdateProperty(new LogEventProperty(name, Redacted));
        }
    }
}
