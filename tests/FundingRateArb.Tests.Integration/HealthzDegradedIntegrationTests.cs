using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Infrastructure.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Tests.Integration;

[Collection("IntegrationTests")]
public class HealthzDegradedIntegrationTests
    : IClassFixture<HealthzDegradedIntegrationTests.DegradedHealthTestFactory>, IDisposable
{
    private readonly HttpClient _client;

    public HealthzDegradedIntegrationTests(DegradedHealthTestFactory factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task Healthz_ReturnsDegraded_WhenDatabaseCheckFails()
    {
        var response = await _client.GetAsync("/healthz");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK,
            "Degraded health status maps to HTTP 200 (only Unhealthy triggers 503)");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("Degraded");
    }

    [Fact]
    public async Task Healthz_DoesNotLeakExceptionDetails_WhenDatabaseCheckFails()
    {
        var response = await _client.GetAsync("/healthz");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().NotContainAny("simulated", "exception", "stack", "SqlException", "InvalidOperation");
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    public class DegradedHealthTestFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = "not-used",
                    ["Seed:AdminPassword"] = "Test@Password1234!"
                });
            });

            builder.ConfigureServices(services =>
            {
                // Replace DbContext with InMemory
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
                    options.UseInMemoryDatabase($"DegradedHealthTest_{Guid.NewGuid()}"));

                // Replace IMarketDataStream with a connected stub so WebSocketStreamHealthCheck
                // reports Healthy (only the database check should be Degraded)
                var streamDescriptors = services
                    .Where(d => d.ServiceType == typeof(IMarketDataStream))
                    .ToList();
                foreach (var d in streamDescriptors)
                {
                    services.Remove(d);
                }

                services.AddSingleton<IMarketDataStream>(new StubMarketDataStream("TestExchange", true));

                // Remove all background hosted services
                var hostedDescriptors = services
                    .Where(d => d.ServiceType == typeof(IHostedService))
                    .ToList();
                foreach (var d in hostedDescriptors)
                {
                    services.Remove(d);
                }

                // Remove IBotControl
                var botControlDescriptors = services
                    .Where(d => d.ServiceType == typeof(IBotControl))
                    .ToList();
                foreach (var d in botControlDescriptors)
                {
                    services.Remove(d);
                }

                // Replace the "database" health check with a stub that throws
                // to simulate a DB failure. The DatabaseHealthCheck.CheckHealthAsync
                // catches all exceptions and returns Degraded — never leaks details.
                // Remove the existing "database" registration first to avoid duplicates.
                services.Configure<HealthCheckServiceOptions>(options =>
                {
                    var existing = options.Registrations
                        .FirstOrDefault(r => r.Name == "database");
                    if (existing is not null)
                    {
                        options.Registrations.Remove(existing);
                    }
                });
                services.AddHealthChecks()
                    .AddCheck<StubDegradedDatabaseHealthCheck>("database");
            });
        }
    }

    private sealed class StubDegradedDatabaseHealthCheck : DatabaseHealthCheck
    {
        public StubDegradedDatabaseHealthCheck(
            IDbContextFactory<AppDbContext> factory,
            ILogger<DatabaseHealthCheck> logger)
            : base(factory, logger) { }

        protected override Task ProbeAsync(CancellationToken ct)
            => throw new InvalidOperationException("simulated DB failure");
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
