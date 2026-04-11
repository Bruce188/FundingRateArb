using System.IO;
using Xunit;

namespace FundingRateArb.Tests.Integration;

/// <summary>
/// Verifies that Program.cs wires the Serilog Application Insights sink correctly:
///   - The sink is registered via <c>WriteTo.ApplicationInsights(..., TelemetryConverter.Traces)</c>.
///   - The connection string is read from <c>ApplicationInsights:ConnectionString</c>.
///   - A startup log line ("Serilog App Insights sink ...") is emitted with the two
///     status literals ("registered" and "not configured (connection string empty)")
///     so operators can grep for them post-deploy.
///   - That startup log line is emitted AFTER <c>builder.Build()</c>, so it flows
///     through the host-configured Serilog pipeline (and therefore the AI sink),
///     not the bootstrap logger.
///
/// The runtime guard behaviour (sink registration does not throw on null / "" / "   " /
/// fake connection string) is covered by
/// <c>FundingRateArb.Tests.Unit.Infrastructure.ApplicationInsightsSinkRegistrationTests</c>.
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
    public void Program_EmitsStartupLogForApplicationInsightsSink()
    {
        var programText = File.ReadAllText(LocateProgramCs());

        Assert.Contains("Serilog App Insights sink", programText);

        // NB3: pin both status literals so a refactor cannot silently break the
        // post-deploy grep runbook.
        Assert.Contains("\"registered\"", programText);
        Assert.Contains("not configured (connection string empty)", programText);
    }

    [Fact]
    public void Program_EmitsStartupLogAfterHostBuild()
    {
        var programText = File.ReadAllText(LocateProgramCs());

        var buildIdx = programText.IndexOf("builder.Build()", StringComparison.Ordinal);
        var logIdx = programText.IndexOf("Serilog App Insights sink", StringComparison.Ordinal);

        Assert.True(buildIdx >= 0, "builder.Build() not found in Program.cs");
        Assert.True(logIdx > buildIdx,
            "Startup confirmation must be emitted after builder.Build() so it flows " +
            "through the host-configured Serilog pipeline (and therefore the AI sink), " +
            "not the bootstrap logger.");
    }
}
