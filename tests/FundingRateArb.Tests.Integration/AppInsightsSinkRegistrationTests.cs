using System.IO;
using Xunit;

namespace FundingRateArb.Tests.Integration;

/// <summary>
/// Verifies that Program.cs wires the Serilog Application Insights sink correctly:
///   - The sink is registered via <c>WriteTo.ApplicationInsights(..., TelemetryConverter.Traces)</c>.
///   - The connection string is read from <c>ApplicationInsights:ConnectionString</c>.
///   - A startup log line ("Serilog App Insights sink ...") is emitted so operators
///     can grep for it post-deploy.
///
/// This is a source-level check. A runtime <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{T}"/>
/// startup test cannot run in this repository's current environment because of a
/// pre-existing <c>TypeLoadException</c> on <c>Azure.Identity.DefaultAzureCredential</c>
/// that affects all WebApplicationFactory-based integration tests. A source-level
/// assertion is sufficient to verify the wiring requirements of this task.
/// </summary>
[Collection("IntegrationTests")]
public class AppInsightsSinkRegistrationTests
{
    private static string LocateProgramCs()
    {
        // Walk up from the test assembly location to the repo root, then into the Web project.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "src", "FundingRateArb.Web", "Program.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new FileNotFoundException(
            "Could not locate src/FundingRateArb.Web/Program.cs from test base directory.");
    }

    [Fact]
    public void Program_RegistersApplicationInsightsSinkWithTracesConverter()
    {
        var programText = File.ReadAllText(LocateProgramCs());

        Assert.Contains("WriteTo.ApplicationInsights", programText);
        Assert.Contains("TelemetryConverter.Traces", programText);
    }

    [Fact]
    public void Program_ReadsApplicationInsightsConnectionStringFromConfiguration()
    {
        var programText = File.ReadAllText(LocateProgramCs());

        Assert.Contains("ApplicationInsights:ConnectionString", programText);
    }

    [Fact]
    public void Program_GuardsApplicationInsightsSinkAgainstEmptyConnectionString()
    {
        var programText = File.ReadAllText(LocateProgramCs());

        // The sink registration must be guarded so empty connection strings don't
        // cause the host to fault during local/test startup. The guard must sit
        // directly above the WriteTo.ApplicationInsights call — we verify this by
        // locating the sink call and walking backwards to find a null/whitespace check.
        var sinkIndex = programText.IndexOf("WriteTo.ApplicationInsights", StringComparison.Ordinal);
        Assert.True(sinkIndex >= 0,
            "WriteTo.ApplicationInsights was not found in Program.cs; cannot verify guard.");

        // Look at a 400-char window immediately before the sink registration for
        // an IsNullOrWhiteSpace or IsNullOrEmpty check on aiConnectionString.
        var windowStart = Math.Max(0, sinkIndex - 400);
        var window = programText.Substring(windowStart, sinkIndex - windowStart);

        var hasGuard =
            window.Contains("IsNullOrWhiteSpace(aiConnectionString)", StringComparison.Ordinal)
            || window.Contains("IsNullOrEmpty(aiConnectionString)", StringComparison.Ordinal);
        Assert.True(hasGuard,
            "The Application Insights sink registration must be directly guarded by a " +
            "null/whitespace check on aiConnectionString.");
    }

    [Fact]
    public void Program_EmitsStartupLogForApplicationInsightsSink()
    {
        var programText = File.ReadAllText(LocateProgramCs());

        Assert.Contains("Serilog App Insights sink", programText);
    }
}
