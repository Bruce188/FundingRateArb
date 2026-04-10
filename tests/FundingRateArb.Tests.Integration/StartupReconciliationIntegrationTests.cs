using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FundingRateArb.Tests.Integration;

[Collection("IntegrationTests")]
public class StartupReconciliationIntegrationTests
{
    private readonly string _dbName = $"ReconciliationTest_{Guid.NewGuid()}";

    [Fact]
    public async Task Reconcile_PhantomOpeningPosition_MarkedFailed()
    {
        // Arrange: seed an Opening position, stub execution engine returns false (not found)
        var positionId = await SeedPositionAsync(PositionStatus.Opening);

        using var factory = CreateFactory(stubExecution: new PhantomExecutionEngineStub());
        using var scope = factory.Services.CreateScope();
        var monitor = scope.ServiceProvider.GetRequiredService<IPositionHealthMonitor>();

        // Act
        await monitor.ReconcileOpenPositionsAsync(CancellationToken.None);

        // Assert: re-read the position from DB
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var position = await db.ArbitragePositions.FindAsync(positionId);

        position.Should().NotBeNull();
        position!.Status.Should().Be(PositionStatus.Failed);
        position.ClosedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Reconcile_OpenPositionWithMissingLegs_MarkedEmergencyClosed()
    {
        // Arrange: seed an Open position, stub execution engine returns BothMissing
        var positionId = await SeedPositionAsync(PositionStatus.Open);

        using var factory = CreateFactory(stubExecution: new DriftExecutionEngineStub(positionId));
        using var scope = factory.Services.CreateScope();
        var monitor = scope.ServiceProvider.GetRequiredService<IPositionHealthMonitor>();

        // Act
        await monitor.ReconcileOpenPositionsAsync(CancellationToken.None);

        // Assert
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var position = await db.ArbitragePositions.FindAsync(positionId);

        position.Should().NotBeNull();
        position!.Status.Should().Be(PositionStatus.EmergencyClosed);
        position.CloseReason.Should().Be(CloseReason.ExchangeDrift);
        position.ClosedAt.Should().NotBeNull();
    }

    private async Task<int> SeedPositionAsync(PositionStatus status)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(_dbName)
            .Options;

        await using var db = new AppDbContext(options);

        // Seed required entities
        var user = new ApplicationUser { Id = "test-user", UserName = "testuser" };
        db.Users.Add(user);

        var longExchange = new Exchange
        {
            Id = 1,
            Name = "Lighter",
            ApiBaseUrl = "https://lighter.test",
            WsBaseUrl = "wss://lighter.test"
        };
        var shortExchange = new Exchange
        {
            Id = 2,
            Name = "Aster",
            ApiBaseUrl = "https://aster.test",
            WsBaseUrl = "wss://aster.test"
        };
        db.Exchanges.Add(longExchange);
        db.Exchanges.Add(shortExchange);

        var asset = new Asset { Id = 1, Symbol = "WLFI", Name = "WLFI" };
        db.Assets.Add(asset);

        var position = new ArbitragePosition
        {
            UserId = "test-user",
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = status,
            SizeUsdc = 1000m,
            MarginUsdc = 500m,
            Leverage = 2,
            LongEntryPrice = 1.0m,
            ShortEntryPrice = 1.0m,
            EntrySpreadPerHour = 0.001m,
            CurrentSpreadPerHour = 0.001m,
            OpenedAt = DateTime.UtcNow.AddHours(-1),
        };
        db.ArbitragePositions.Add(position);

        db.BotConfigurations.Add(new BotConfiguration
        {
            IsEnabled = false,
            UpdatedByUserId = "test-user"
        });

        await db.SaveChangesAsync();
        return position.Id;
    }

    private WebApplicationFactory<Program> CreateFactory(IExecutionEngine stubExecution)
    {
        return new ReconciliationTestFactory(_dbName, stubExecution);
    }

    private sealed class ReconciliationTestFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName;
        private readonly IExecutionEngine _stubExecution;

        public ReconciliationTestFactory(string dbName, IExecutionEngine stubExecution)
        {
            _dbName = dbName;
            _stubExecution = stubExecution;
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
                // Replace DbContext with the same in-memory DB that was seeded
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

                // Replace IExecutionEngine with stub
                var executionDescriptors = services
                    .Where(d => d.ServiceType == typeof(IExecutionEngine))
                    .ToList();
                foreach (var d in executionDescriptors)
                {
                    services.Remove(d);
                }

                services.AddScoped<IExecutionEngine>(_ => _stubExecution);
            });
        }
    }

    /// <summary>Stub for Opening position test: CheckPositionExistsOnExchangesAsync returns false.</summary>
    private sealed class PhantomExecutionEngineStub : IExecutionEngine
    {
        public Task<(bool Success, string? Error)> OpenPositionAsync(
            string userId, ArbitrageOpportunityDto opp, decimal sizeUsdc,
            UserConfiguration? userConfig = null, CancellationToken ct = default)
            => Task.FromResult((false, (string?)"stub"));

        public Task ClosePositionAsync(string userId, ArbitragePosition position,
            CloseReason reason, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool?> CheckPositionExistsOnExchangesAsync(
            ArbitragePosition position, CancellationToken ct = default)
            => Task.FromResult<bool?>(false);

        public Task<Dictionary<int, PositionExistsResult>> CheckPositionsExistOnExchangesBatchAsync(
            IReadOnlyList<ArbitragePosition> positions, CancellationToken ct = default)
            => Task.FromResult(new Dictionary<int, PositionExistsResult>());
    }

    /// <summary>Stub for Open position test: batch returns BothMissing for the seeded position.</summary>
    private sealed class DriftExecutionEngineStub : IExecutionEngine
    {
        private readonly int _positionId;

        public DriftExecutionEngineStub(int positionId) => _positionId = positionId;

        public Task<(bool Success, string? Error)> OpenPositionAsync(
            string userId, ArbitrageOpportunityDto opp, decimal sizeUsdc,
            UserConfiguration? userConfig = null, CancellationToken ct = default)
            => Task.FromResult((false, (string?)"stub"));

        public Task ClosePositionAsync(string userId, ArbitragePosition position,
            CloseReason reason, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool?> CheckPositionExistsOnExchangesAsync(
            ArbitragePosition position, CancellationToken ct = default)
            => Task.FromResult<bool?>(false);

        public Task<Dictionary<int, PositionExistsResult>> CheckPositionsExistOnExchangesBatchAsync(
            IReadOnlyList<ArbitragePosition> positions, CancellationToken ct = default)
        {
            var result = new Dictionary<int, PositionExistsResult>();
            foreach (var p in positions)
            {
                result[p.Id] = p.Id == _positionId
                    ? PositionExistsResult.BothMissing
                    : PositionExistsResult.BothPresent;
            }
            return Task.FromResult(result);
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
