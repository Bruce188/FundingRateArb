using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FundingRateArb.Tests.Integration;

[Collection("IntegrationTests")]
public class DatabaseHealthCheckIntegrationTests
    : IClassFixture<DatabaseHealthCheckIntegrationTests.RetryLessHealthCheckFactory>
{
    private readonly RetryLessHealthCheckFactory _factory;

    public DatabaseHealthCheckIntegrationTests(RetryLessHealthCheckFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void KeyedFactory_ResolvesRetryLessOptions()
    {
        // Resolve the keyed factory from the test host's DI container.
        using var scope = _factory.Services.CreateScope();
        var keyedFactory = scope.ServiceProvider
            .GetKeyedService<IDbContextFactory<AppDbContext>>("health-check");

        keyedFactory.Should().NotBeNull(
            "the 'health-check' keyed IDbContextFactory<AppDbContext> must be registered in DI");

        // Create a context — no SQL connection is opened until a query is executed.
        using var context = keyedFactory!.CreateDbContext();

        // Inspect the relational options extension to verify no retry strategy is configured.
        var dbOptions = context.GetService<IDbContextOptions>();
        var relationalExt = RelationalOptionsExtension.Extract(dbOptions);

        relationalExt.Should().NotBeNull(
            "the health-check factory must use a relational provider");
        relationalExt!.ExecutionStrategyFactory.Should().BeNull(
            "health-check factory must not have EnableRetryOnFailure — a custom ExecutionStrategyFactory " +
            "is only set when EnableRetryOnFailure is configured, and retries must never hold the health probe open");
    }

    public class RetryLessHealthCheckFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] =
                        "Server=localhost;Database=FakeDb;Trusted_Connection=True;",
                    ["Seed:AdminPassword"] = "Test@Password1234!"
                });
            });

            builder.ConfigureServices(services =>
            {
                // Replace DbContext registrations with InMemory so the app starts
                // without a real SQL Server, while leaving the keyed "health-check"
                // factory in place (it is registered separately as a keyed singleton
                // and is not affected by removing the non-keyed DbContextOptions).
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

                services.AddDbContext<AppDbContext>(options =>
                    options.UseInMemoryDatabase($"HealthCheckIntegrationTest_{Guid.NewGuid()}"));

                // Replace IMarketDataStream with a stub so the app starts cleanly.
                var streamDescriptors = services
                    .Where(d => d.ServiceType == typeof(IMarketDataStream))
                    .ToList();
                foreach (var d in streamDescriptors)
                {
                    services.Remove(d);
                }

                services.AddSingleton<IMarketDataStream>(new StubMarketDataStream("TestExchange", true));

                // Remove background hosted services to avoid SQL connection attempts at startup.
                var hostedDescriptors = services
                    .Where(d => d.ServiceType == typeof(IHostedService))
                    .ToList();
                foreach (var d in hostedDescriptors)
                {
                    services.Remove(d);
                }

                // Remove IBotControl which depends on BotOrchestrator.
                var botControlDescriptors = services
                    .Where(d => d.ServiceType == typeof(IBotControl))
                    .ToList();
                foreach (var d in botControlDescriptors)
                {
                    services.Remove(d);
                }
            });
        }
    }

    private sealed class StubMarketDataStream : IMarketDataStream
    {
        public StubMarketDataStream(string exchangeName, bool isConnected)
        {
            ExchangeName = exchangeName;
            IsConnected = isConnected;
        }

        public string ExchangeName { get; }
        public bool IsConnected { get; }

#pragma warning disable CS0067
        public event Action<FundingRateDto>? OnRateUpdate;
        public event Action<string, string>? OnDisconnected;
#pragma warning restore CS0067

        public Task StartAsync(IEnumerable<string> symbols, CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
