using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Domain.ValueObjects;
using FundingRateArb.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FundingRateArb.Tests.Integration;

[Collection("IntegrationTests")]
public class WlfiPairScenarioTests
{
    private readonly string _dbName = $"WlfiPairTest_{Guid.NewGuid()}";

    [Fact]
    public async Task WlfiPair_ExceedsAsterNotionalCap_FilteredAtScreening()
    {
        await SeedDatabaseAsync();

        // Notional cap: sizedNotional = 10_000 * 0.5 * 3 = 15_000 > 1_000 → filtered
        var constraints = new ConfigurableConstraintsProvider(maxNotional: 1_000m);
        using var factory = CreateFactory(constraints);

        using var scope = factory.Services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<ISignalEngine>();

        var result = await engine.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Diagnostics!.PairsFilteredByExchangeSymbolCap.Should().BeGreaterOrEqualTo(1);
        result.Opportunities.Should().NotContain(o => o.AssetSymbol == "WLFI");
    }

    [Fact]
    public async Task WlfiPair_WithinAsterNotionalCap_PassesThrough()
    {
        await SeedDatabaseAsync();

        // Notional cap: sizedNotional = 10_000 * 0.5 * 3 = 15_000 < 1_000_000 → passes
        var constraints = new ConfigurableConstraintsProvider(maxNotional: 1_000_000m);
        using var factory = CreateFactory(constraints);

        using var scope = factory.Services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<ISignalEngine>();

        var result = await engine.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Diagnostics!.PairsFilteredByExchangeSymbolCap.Should().Be(0);
        result.Opportunities.Should().Contain(o => o.AssetSymbol == "WLFI");
    }

    private async Task SeedDatabaseAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(_dbName)
            .Options;

        await using var db = new AppDbContext(options);

        // Seed exchanges (Lighter and Aster)
        var lighter = new Exchange
        {
            Id = 1,
            Name = "Lighter",
            ApiBaseUrl = "https://lighter.test",
            WsBaseUrl = "wss://lighter.test",
            FundingIntervalHours = 1,
            TakerFeeRate = 0.00045m,
        };
        var aster = new Exchange
        {
            Id = 2,
            Name = "Aster",
            ApiBaseUrl = "https://aster.test",
            WsBaseUrl = "wss://aster.test",
            FundingIntervalHours = 8,
            TakerFeeRate = 0.00045m,
        };
        db.Exchanges.Add(lighter);
        db.Exchanges.Add(aster);

        // Seed asset
        var asset = new Asset { Id = 1, Symbol = "WLFI", Name = "WLFI" };
        db.Assets.Add(asset);

        // Seed user
        var user = new ApplicationUser { Id = "test-user", UserName = "testuser" };
        db.Users.Add(user);

        // Seed BotConfiguration with values that allow the pair through all filters
        db.BotConfigurations.Add(new BotConfiguration
        {
            IsEnabled = true,
            UpdatedByUserId = "test-user",
            MinVolume24hUsdc = 100_000m,
            BreakevenHoursMax = 48,
            OpenThreshold = 0.0001m,
            MaxCapitalPerPosition = 0.50m,
            TotalCapitalUsdc = 10_000m,
            DefaultLeverage = 3,
            MaxLeverageCap = 50,
            SlippageBufferBps = 0,
            MinEdgeMultiplier = 1m,
            RateStalenessMinutes = 15,
            FeeAmortizationHours = 12,
            MinConsecutiveFavorableCycles = 1,
        });

        // Seed funding rate snapshots with a spread that passes filters.
        // Lighter: low rate (long leg). Aster: higher rate (short leg).
        // Spread = shortRate - longRate = 0.0010 - 0.0001 = 0.0009 per hour.
        db.FundingRateSnapshots.Add(new FundingRateSnapshot
        {
            ExchangeId = 1,
            AssetId = 1,
            RatePerHour = 0.0001m,
            RawRate = 0.0001m,
            MarkPrice = 1.0m,
            IndexPrice = 1.0m,
            Volume24hUsd = 500_000m,
            RecordedAt = DateTime.UtcNow,
        });
        db.FundingRateSnapshots.Add(new FundingRateSnapshot
        {
            ExchangeId = 2,
            AssetId = 1,
            RatePerHour = 0.0010m,
            RawRate = 0.0010m,
            MarkPrice = 1.0m,
            IndexPrice = 1.0m,
            Volume24hUsd = 500_000m,
            RecordedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
    }

    private WebApplicationFactory<Program> CreateFactory(
        IExchangeSymbolConstraintsProvider constraints)
    {
        return new WlfiTestFactory(_dbName, constraints);
    }

    private sealed class WlfiTestFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName;
        private readonly IExchangeSymbolConstraintsProvider _constraints;

        public WlfiTestFactory(string dbName, IExchangeSymbolConstraintsProvider constraints)
        {
            _dbName = dbName;
            _constraints = constraints;
        }

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
                // Replace DbContext with the pre-seeded in-memory DB
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
                    options.UseInMemoryDatabase(_dbName));

                // Replace IMarketDataStream
                var streamDescriptors = services
                    .Where(d => d.ServiceType == typeof(IMarketDataStream))
                    .ToList();
                foreach (var d in streamDescriptors)
                {
                    services.Remove(d);
                }

                services.AddSingleton<IMarketDataStream>(new StubMarketDataStream("TestExchange", true));

                // Remove all hosted services
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

                // Replace IExchangeSymbolConstraintsProvider
                var constraintDescriptors = services
                    .Where(d => d.ServiceType == typeof(IExchangeSymbolConstraintsProvider))
                    .ToList();
                foreach (var d in constraintDescriptors)
                {
                    services.Remove(d);
                }

                services.AddSingleton<IExchangeSymbolConstraintsProvider>(_constraints);

                // Replace IMarketDataCache with stub that returns next settlement times
                var cacheDescriptors = services
                    .Where(d => d.ServiceType == typeof(IMarketDataCache))
                    .ToList();
                foreach (var d in cacheDescriptors)
                {
                    services.Remove(d);
                }

                services.AddSingleton<IMarketDataCache>(new StubMarketDataCache());

                // Replace optional services with stubs (other services depend on them,
                // so we can't just remove them — DI validation will fail)
                var tierDescriptors = services
                    .Where(d => d.ServiceType == typeof(ILeverageTierProvider))
                    .ToList();
                foreach (var d in tierDescriptors)
                {
                    services.Remove(d);
                }

                services.AddSingleton<ILeverageTierProvider>(new StubLeverageTierProvider());
            });
        }
    }

    private sealed class ConfigurableConstraintsProvider : IExchangeSymbolConstraintsProvider
    {
        private readonly decimal _maxNotional;

        public ConfigurableConstraintsProvider(decimal maxNotional) => _maxNotional = maxNotional;

        public Task<decimal?> GetMaxNotionalAsync(string exchangeName, string symbol, CancellationToken ct = default)
        {
            // Only Aster has a notional cap
            if (exchangeName.Equals("Aster", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<decimal?>(_maxNotional);
            }

            return Task.FromResult<decimal?>(null);
        }
    }

    private sealed class StubMarketDataCache : IMarketDataCache
    {
        public void Update(FundingRateDto rate) { }
        public FundingRateDto? GetLatest(string exchangeName, string symbol) => null;
        public List<FundingRateDto> GetAllLatest() => [];
        public List<FundingRateDto> GetAllForExchange(string exchangeName) => [];
        public decimal GetMarkPrice(string exchangeName, string symbol) => 1.0m;

        public DateTime? GetNextSettlement(string exchangeName, string symbol)
            => DateTime.UtcNow.AddHours(1);

        public bool IsStale(string exchangeName, string symbol, TimeSpan maxAge) => false;
        public bool IsStaleForExchange(string exchangeName, TimeSpan maxAge) => false;
        public DateTime? GetLastFetchTime() => null;
    }

    private sealed class StubLeverageTierProvider : ILeverageTierProvider
    {
        public Task<LeverageTier[]> GetTiersAsync(string exchangeName, string asset, CancellationToken ct = default)
            => Task.FromResult(Array.Empty<LeverageTier>());

        public int GetEffectiveMaxLeverage(string exchangeName, string asset, decimal notionalUsdc) => 50;
        public decimal GetMaintenanceMarginRate(string exchangeName, string asset, decimal notionalUsdc) => 0.01m;
        public void UpdateTiers(string exchangeName, string asset, LeverageTier[] tiers) { }
        public bool IsStale(string exchangeName, string asset) => false;
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
