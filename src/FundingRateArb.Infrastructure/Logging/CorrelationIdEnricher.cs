using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Serilog.Core;
using Serilog.Events;

namespace FundingRateArb.Infrastructure.Logging;

/// <summary>
/// Serilog enricher that adds a CorrelationId property to every log entry.
/// Uses Activity.Current?.Id (W3C trace context), falls back to HttpContext.TraceIdentifier,
/// and generates a GUID when neither is available (e.g., background services at startup).
/// </summary>
public class CorrelationIdEnricher : ILogEventEnricher
{
    private const string PropertyName = "CorrelationId";
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CorrelationIdEnricher(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var correlationId = Activity.Current?.Id
            ?? _httpContextAccessor.HttpContext?.TraceIdentifier
            ?? Guid.NewGuid().ToString();

        var property = propertyFactory.CreateProperty(PropertyName, correlationId);
        logEvent.AddPropertyIfAbsent(property);
    }
}
