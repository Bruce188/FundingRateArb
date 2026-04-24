using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Tests.Integration.Startup;

/// <summary>
/// Runtime integration tests for the flag-gated Production seed branch in Program.cs.
///
/// Strategy: admin-user row presence in the in-memory database after startup.
/// The row is absent when the gate condition is false and present when both
/// Seed:ForceAdminPasswordReset == true and Seed:AdminPassword is non-empty.
///
/// Config note: the gate condition reads from <c>builder.Configuration</c> (the
/// WebApplicationBuilder instance), not from the DI-resolved IConfiguration.
/// WebApplicationFactory's ConfigureAppConfiguration callback feeds the DI store but
/// not the builder instance, so environment variables (loaded before CreateBuilder)
/// are the only reliable way to reach that code path from tests.
/// </summary>
[Collection("IntegrationTests")]
public class ProductionSeedGateTests
{
    private const string FlagEnvVar = "Seed__ForceAdminPasswordReset";
    private const string PasswordEnvVar = "Seed__AdminPassword";
    private const string AdminEmail = "admin@fundingratearb.com";
    private const string AdminPassword = "Test!Passw0rd";

    // -------------------------------------------------------------------------
    // 1. Flag unset → seed did NOT run
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FlagUnset_SeedDoesNotRun()
    {
        // Neither env var is set — the else-if condition evaluates false.
        await using var factory = CreateFactory();
        factory.CreateClient();

        var admin = await FindAdminAsync(factory);
        admin.Should().BeNull("seeder must not run when Seed:ForceAdminPasswordReset is absent");
    }

    // -------------------------------------------------------------------------
    // 2. Flag true, Seed:AdminPassword empty/missing → seed did NOT run
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FlagSetPasswordEmpty_SeedDoesNotRun()
    {
        Environment.SetEnvironmentVariable(FlagEnvVar, "true");
        try
        {
            await using var factory = CreateFactory();
            factory.CreateClient();

            var admin = await FindAdminAsync(factory);
            admin.Should().BeNull(
                "seeder must not run when Seed:ForceAdminPasswordReset=true but Seed:AdminPassword is absent");
        }
        finally
        {
            Environment.SetEnvironmentVariable(FlagEnvVar, null);
        }
    }

    // -------------------------------------------------------------------------
    // 3. Flag true, Seed:AdminPassword = "Test!Passw0rd" → seed ran
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FlagSetPasswordSet_SeedRuns()
    {
        Environment.SetEnvironmentVariable(FlagEnvVar, "true");
        Environment.SetEnvironmentVariable(PasswordEnvVar, AdminPassword);
        try
        {
            await using var factory = CreateFactory();
            factory.CreateClient();

            var admin = await FindAdminAsync(factory);
            admin.Should().NotBeNull(
                "seeder must run and create the admin user when both " +
                "Seed:ForceAdminPasswordReset=true and Seed:AdminPassword is non-empty");

        }
        finally
        {
            Environment.SetEnvironmentVariable(FlagEnvVar, null);
            Environment.SetEnvironmentVariable(PasswordEnvVar, null);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static SeedGateFactory CreateFactory() => new();

    private static async Task<ApplicationUser?> FindAdminAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await userManager.FindByEmailAsync(AdminEmail);
    }

    // -------------------------------------------------------------------------
    // Factory
    // -------------------------------------------------------------------------

    private sealed class SeedGateFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");

            // Override the connection string so the CHANGE_ME guard in Program.cs
            // does not throw. ConfigureAppConfiguration feeds app.Configuration (DI)
            // which is where that guard reads from.
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = "Server=not-used",
                }));

            builder.ConfigureServices(services =>
            {
                // In Production the Serilog MSSqlServer sink (AutoCreateSqlTable=true)
                // connects synchronously during Build() when the logger factory is
                // resolved. Remove all ILoggerFactory/ILoggerProvider registrations and
                // replace with a console-only provider to avoid the SQL Server connection.
                var logFactories = services.Where(d => d.ServiceType == typeof(ILoggerFactory)).ToList();
                foreach (var d in logFactories)
                {
                    services.Remove(d);
                }

                var logProviders = services.Where(d => d.ServiceType == typeof(ILoggerProvider)).ToList();
                foreach (var d in logProviders)
                {
                    services.Remove(d);
                }

                services.AddLogging(lb => lb.AddConsole().SetMinimumLevel(LogLevel.Warning));

                // Swap SQL Server DbContext with an isolated in-memory store so the
                // seeder can run without a real database and remain observable post-startup.
                // The database name is captured outside the lambda so that all scopes
                // (seed scope in Program.cs + query scope in tests) share the same store.
                // If the name were generated inside the lambda it would be re-evaluated
                // once per scope (AddDbContext uses optionsLifetime=Scoped by default).
                var seedDbName = $"SeedGateTest_{Guid.NewGuid()}";
                var efDescriptors = services
                    .Where(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>)
                             || d.ServiceType == typeof(AppDbContext)
                             || (d.ServiceType.IsGenericType &&
                                 d.ServiceType.GetGenericTypeDefinition() == typeof(DbContextOptions<>)))
                    .ToList();
                foreach (var d in efDescriptors)
                {
                    services.Remove(d);
                }

                services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(seedDbName));

                // Stub IMarketDataStream so exchange connectors do not attempt outbound connections.
                foreach (var d in services.Where(d => d.ServiceType == typeof(IMarketDataStream)).ToList())
                {
                    services.Remove(d);
                }

                services.AddSingleton<IMarketDataStream>(new StubMarketDataStream());

                // Remove background hosted services.
                foreach (var d in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
                {
                    services.Remove(d);
                }

                // IBotControl forwards to BotOrchestrator which was removed above.
                foreach (var d in services.Where(d => d.ServiceType == typeof(IBotControl)).ToList())
                {
                    services.Remove(d);
                }
            });
        }
    }

    private sealed class StubMarketDataStream : IMarketDataStream
    {
        public string ExchangeName => "Stub";
        public bool IsConnected => false;

#pragma warning disable CS0067
        public event Action<FundingRateDto>? OnRateUpdate;
        public event Action<string, string>? OnDisconnected;
#pragma warning restore CS0067

        public Task StartAsync(IEnumerable<string> symbols, CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
