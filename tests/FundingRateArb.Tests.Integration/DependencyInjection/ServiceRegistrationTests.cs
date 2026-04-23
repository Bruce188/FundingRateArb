using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Common.Services;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FundingRateArb.Tests.Integration.DependencyInjection;

/// <summary>
/// Asserts that IFundingRateRepository is registered exactly once with ServiceLifetime.Scoped
/// and can be resolved from a child scope. Prevents regression if the registration is
/// accidentally removed or the lifetime is changed.
/// </summary>
[Collection("IntegrationTests")]
public class ServiceRegistrationTests : IClassFixture<ServiceRegistrationTests.ServiceRegistrationFactory>
{
    private readonly ServiceRegistrationFactory _factory;

    public ServiceRegistrationTests(ServiceRegistrationFactory factory)
    {
        _factory = factory;
        // Trigger host build so that CapturedServices is populated before any test runs.
        _ = factory.Services;
    }

    [Fact]
    public void IFundingRateRepository_HasExactlyOneDescriptor()
    {
        var count = _factory.CapturedServices
            .Count(d => d.ServiceType == typeof(IFundingRateRepository));

        Assert.Equal(1, count);
    }

    [Fact]
    public void IFundingRateRepository_IsRegisteredAsScoped()
    {
        var descriptor = _factory.CapturedServices
            .Single(d => d.ServiceType == typeof(IFundingRateRepository));

        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void IFundingRateRepository_ResolvesFromChildScope()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IFundingRateRepository>();

        Assert.NotNull(repo);
    }

    [Fact]
    public void IDatabaseSpaceHealthProbe_IsRegisteredAsScoped()
    {
        var descriptor = _factory.CapturedServices
            .Single(d => d.ServiceType == typeof(IDatabaseSpaceHealthProbe));

        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    public class ServiceRegistrationFactory : WebApplicationFactory<Program>
    {
        public IReadOnlyList<ServiceDescriptor> CapturedServices { get; private set; } = [];

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
                // Capture service descriptors before any further modification
                CapturedServices = services.ToList();

                // Replace DbContext with InMemory so the DI graph can be built
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
                    options.UseInMemoryDatabase($"ServiceRegistrationTest_{Guid.NewGuid()}"));

                // Replace IMarketDataStream with stub so the host builds cleanly
                var streamDescriptors = services
                    .Where(d => d.ServiceType == typeof(IMarketDataStream))
                    .ToList();
                foreach (var d in streamDescriptors)
                {
                    services.Remove(d);
                }

                services.AddSingleton<IMarketDataStream>(new StubMarketDataStream());

                // Remove all background hosted services to avoid real-time side-effects
                var hostedDescriptors = services
                    .Where(d => d.ServiceType == typeof(IHostedService))
                    .ToList();
                foreach (var d in hostedDescriptors)
                {
                    services.Remove(d);
                }

                // Remove IBotControl which depends on BotOrchestrator
                var botControlDescriptors = services
                    .Where(d => d.ServiceType == typeof(Application.Interfaces.IBotControl))
                    .ToList();
                foreach (var d in botControlDescriptors)
                {
                    services.Remove(d);
                }
            });
        }

        private sealed class StubMarketDataStream : IMarketDataStream
        {
            public string ExchangeName => "TestExchange";
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
}
