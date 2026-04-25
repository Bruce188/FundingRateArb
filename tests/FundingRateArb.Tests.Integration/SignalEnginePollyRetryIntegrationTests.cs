using FluentAssertions;
using FundingRateArb.Application.Common;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FundingRateArb.Tests.Integration;

[Collection("IntegrationTests")]
public class SignalEnginePollyRetryIntegrationTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public SignalEnginePollyRetryIntegrationTests()
    {
        _factory = new BaseFactory();
    }

    [Fact]
    public async Task SignalEngine_DatabaseUnavailable_ReturnsDegradedResult()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                ReplaceUnitOfWork(services, new AlwaysFailingUnitOfWork());
            });
        });

        using var scope = factory.Services.CreateScope();
        var signalEngine = scope.ServiceProvider.GetRequiredService<ISignalEngine>();

        var result = await signalEngine.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.DatabaseAvailable.Should().BeFalse();
        result.FailureReason.Should().Be(SignalEngineFailureReason.DatabaseUnavailable);
        result.Opportunities.Should().BeEmpty();
    }

    [Fact]
    public async Task SignalEngine_TransientRecovery_ReturnsSuccessAfterFailure()
    {
        var counterUow = new CounterBasedUnitOfWork();

        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                ReplaceUnitOfWork(services, counterUow);
            });
        });

        // First call — should fail (counter-based stub throws on first call)
        using (var scope1 = factory.Services.CreateScope())
        {
            var engine1 = scope1.ServiceProvider.GetRequiredService<ISignalEngine>();
            var result1 = await engine1.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

            result1.IsSuccess.Should().BeFalse();
            result1.DatabaseAvailable.Should().BeFalse();
        }

        // Allow the degraded-result short TTL (500 ms) to elapse so the cache self-heals.
        // SignalEngine caches degraded results with a 500 ms TTL to avoid hammering the
        // failing dependency, while still recovering quickly once it is restored.
        await Task.Delay(TimeSpan.FromMilliseconds(600));

        // Second call — should succeed after TTL expiry
        using (var scope2 = factory.Services.CreateScope())
        {
            var engine2 = scope2.ServiceProvider.GetRequiredService<ISignalEngine>();
            var result2 = await engine2.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

            result2.IsSuccess.Should().BeTrue();
            result2.DatabaseAvailable.Should().BeTrue();
        }
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private static void ReplaceUnitOfWork(IServiceCollection services, IUnitOfWork replacement)
    {
        var descriptors = services
            .Where(d => d.ServiceType == typeof(IUnitOfWork))
            .ToList();
        foreach (var d in descriptors)
        {
            services.Remove(d);
        }

        services.AddScoped<IUnitOfWork>(_ => replacement);
    }

    private sealed class BaseFactory : WebApplicationFactory<Program>
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
                    options.UseInMemoryDatabase($"SignalEngineRetryTest_{Guid.NewGuid()}"));

                var streamDescriptors = services
                    .Where(d => d.ServiceType == typeof(IMarketDataStream))
                    .ToList();
                foreach (var d in streamDescriptors)
                {
                    services.Remove(d);
                }

                services.AddSingleton<IMarketDataStream>(new StubMarketDataStream("TestExchange", true));

                var hostedDescriptors = services
                    .Where(d => d.ServiceType == typeof(IHostedService))
                    .ToList();
                foreach (var d in hostedDescriptors)
                {
                    services.Remove(d);
                }

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

    /// <summary>
    /// UoW stub that always throws DatabaseUnavailableException from BotConfig.GetActiveAsync.
    /// </summary>
    private sealed class AlwaysFailingUnitOfWork : IUnitOfWork
    {
        public IExchangeRepository Exchanges => throw new InvalidOperationException("Not expected in this test scenario");
        public IAssetRepository Assets => throw new InvalidOperationException("Not expected in this test scenario");
        public IFundingRateRepository FundingRates => throw new InvalidOperationException("Not expected in this test scenario");
        public IPositionRepository Positions => throw new InvalidOperationException("Not expected in this test scenario");
        public IAlertRepository Alerts => throw new InvalidOperationException("Not expected in this test scenario");
        public IBotConfigRepository BotConfig => new FailingBotConfigRepository();
        public IExchangeAssetConfigRepository ExchangeAssetConfigs => throw new InvalidOperationException("Not expected in this test scenario");
        public IUserExchangeCredentialRepository UserCredentials => throw new InvalidOperationException("Not expected in this test scenario");
        public IUserConfigurationRepository UserConfigurations => throw new InvalidOperationException("Not expected in this test scenario");
        public IUserPreferenceRepository UserPreferences => throw new InvalidOperationException("Not expected in this test scenario");
        public IOpportunitySnapshotRepository OpportunitySnapshots => throw new InvalidOperationException("Not expected in this test scenario");
        public Task<int> SaveAsync(CancellationToken ct = default) => Task.FromResult(0);
        public void Dispose() { }

        private sealed class FailingBotConfigRepository : IBotConfigRepository
        {
            public Task<BotConfiguration> GetActiveAsync()
                => throw new DatabaseUnavailableException("simulated login failure");
            public Task<BotConfiguration> GetActiveTrackedAsync()
                => throw new DatabaseUnavailableException("simulated login failure");
            public void Update(BotConfiguration config) { }
            public void InvalidateCache() { }
        }
    }

    /// <summary>
    /// UoW stub that throws on first call, then returns valid data on subsequent calls.
    /// </summary>
    private sealed class CounterBasedUnitOfWork : IUnitOfWork
    {
        private int _callCount;

        public IExchangeRepository Exchanges => throw new InvalidOperationException("Not expected in this test scenario");
        public IAssetRepository Assets => throw new InvalidOperationException("Not expected in this test scenario");
        public IFundingRateRepository FundingRates => new CounterFundingRateRepository(this);
        public IPositionRepository Positions => throw new InvalidOperationException("Not expected in this test scenario");
        public IAlertRepository Alerts => throw new InvalidOperationException("Not expected in this test scenario");
        public IBotConfigRepository BotConfig => new CounterBotConfigRepository(this);
        public IExchangeAssetConfigRepository ExchangeAssetConfigs => throw new InvalidOperationException("Not expected in this test scenario");
        public IUserExchangeCredentialRepository UserCredentials => throw new InvalidOperationException("Not expected in this test scenario");
        public IUserConfigurationRepository UserConfigurations => new EmptyUserConfigurationRepository();
        public IUserPreferenceRepository UserPreferences => throw new InvalidOperationException("Not expected in this test scenario");
        public IOpportunitySnapshotRepository OpportunitySnapshots => throw new InvalidOperationException("Not expected in this test scenario");
        public Task<int> SaveAsync(CancellationToken ct = default) => Task.FromResult(0);
        public void Dispose() { }

        private bool ShouldFail()
        {
            return Interlocked.Increment(ref _callCount) <= 1; // First call fails, subsequent succeed
        }

        private sealed class CounterBotConfigRepository : IBotConfigRepository
        {
            private readonly CounterBasedUnitOfWork _owner;
            public CounterBotConfigRepository(CounterBasedUnitOfWork owner) => _owner = owner;

            public Task<BotConfiguration> GetActiveAsync()
            {
                if (_owner.ShouldFail())
                {
                    throw new DatabaseUnavailableException("simulated transient failure");
                }

                return Task.FromResult(new BotConfiguration
                {
                    IsEnabled = false,
                    UpdatedByUserId = "seed",
                    TotalCapitalUsdc = 10_000m,
                    MaxCapitalPerPosition = 0.5m,
                    DefaultLeverage = 3,
                    MaxLeverageCap = 3,
                    MinVolume24hUsdc = 50_000m,
                    OpenThreshold = 0.0002m,
                    RateStalenessMinutes = 15,
                });
            }

            public Task<BotConfiguration> GetActiveTrackedAsync() => GetActiveAsync();
            public void Update(BotConfiguration config) { }
            public void InvalidateCache() { }
        }

        private sealed class CounterFundingRateRepository : IFundingRateRepository
        {
            private readonly CounterBasedUnitOfWork _owner;
            public CounterFundingRateRepository(CounterBasedUnitOfWork owner) => _owner = owner;

            public Task<List<FundingRateSnapshot>> GetLatestPerExchangePerAssetAsync()
                => Task.FromResult(new List<FundingRateSnapshot>());

            public Task<List<FundingRateSnapshot>> GetHistoryAsync(int assetId, int exchangeId,
                DateTime from, DateTime to, int take = 1000, int skip = 0)
                => Task.FromResult(new List<FundingRateSnapshot>());

            public void Add(FundingRateSnapshot snapshot) { }
            public void AddRange(IEnumerable<FundingRateSnapshot> snapshots) { }
            public Task<int> PurgeOlderThanAsync(DateTime cutoff, bool force = false, CancellationToken ct = default) => Task.FromResult(0);
            public int GetSuppressedPurgeCount() => 0;
            public Task<List<FundingRateHourlyAggregate>> GetHourlyAggregatesAsync(
                int? assetId, int? exchangeId, DateTime from, DateTime to, CancellationToken ct = default)
                => Task.FromResult(new List<FundingRateHourlyAggregate>());
            public Task<bool> HourlyAggregatesExistAsync(DateTime from, DateTime to, CancellationToken ct = default)
                => Task.FromResult(false);
            public void AddAggregateRange(IEnumerable<FundingRateHourlyAggregate> aggregates) { }
            public Task<int> PurgeAggregatesOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
                => Task.FromResult(0);
            public Task<List<FundingRateSnapshot>> GetSnapshotsInRangeAsync(
                DateTime from, DateTime to, CancellationToken ct = default)
                => Task.FromResult(new List<FundingRateSnapshot>());
            public Task<List<FundingRateHourlyAggregate>> GetLatestAggregatePerAssetExchangeAsync(CancellationToken ct = default)
                => Task.FromResult(new List<FundingRateHourlyAggregate>());
            public Task<List<(int AssetId, int ExchangeId, decimal Mean, decimal StdDev)>> GetAggregateStatsByPairAsync(
                DateTime from, DateTime to, CancellationToken ct = default)
                => Task.FromResult(new List<(int, int, decimal, decimal)>());
        }

        private sealed class EmptyUserConfigurationRepository : IUserConfigurationRepository
        {
            public Task<UserConfiguration?> GetByUserAsync(string userId) => Task.FromResult<UserConfiguration?>(null);
            public Task<List<string>> GetAllEnabledUserIdsAsync() => Task.FromResult(new List<string>());
            public void Add(UserConfiguration config) { }
            public void Update(UserConfiguration config) { }
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
