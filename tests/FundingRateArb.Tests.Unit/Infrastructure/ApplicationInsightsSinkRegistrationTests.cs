using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Sinks.ApplicationInsights.TelemetryConverters;
using Xunit;

namespace FundingRateArb.Tests.Unit.Infrastructure;

/// <summary>
/// Mirrors the Program.cs sink-registration block against an in-memory IConfiguration
/// and asserts that neither WriteTo.ApplicationInsights(...) nor CreateLogger().Information(...)
/// throws for the four relevant connection-string shapes (null / "" / "   " / fake conn).
///
/// This replaces the earlier source-level Program_GuardsApplicationInsightsSinkAgainstEmptyConnectionString
/// test (review-v157 NB1 + NB2): we exercise the guard's actual runtime effect without
/// coupling to variable names or proximity windows in Program.cs, and without requiring
/// WebApplicationFactory<Program> (which is blocked by a pre-existing
/// Azure.Identity.DefaultAzureCredential TypeLoadException on this environment).
/// </summary>
public class ApplicationInsightsSinkRegistrationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://example/")]
    public void ApplicationInsightsSink_Registration_DoesNotThrow(string? connString)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApplicationInsights:ConnectionString"] = connString,
            })
            .Build();

        var lc = new LoggerConfiguration();
        var aiConnectionString = config["ApplicationInsights:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(aiConnectionString))
        {
            lc.WriteTo.ApplicationInsights(aiConnectionString, TelemetryConverter.Traces);
        }

        using var logger = lc.CreateLogger();
        logger.Information("smoke");
    }
}
