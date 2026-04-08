using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.BackgroundServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.BackgroundServices;

public class LeverageTierRefresherTests
{
    private static FundingRateSnapshot MakeRate(int exchangeId, string exchangeName, int assetId, string symbol)
    {
        return new FundingRateSnapshot
        {
            ExchangeId = exchangeId,
            AssetId = assetId,
            RatePerHour = 0.0001m,
            Exchange = new Exchange { Id = exchangeId, Name = exchangeName },
            Asset = new Asset { Id = assetId, Symbol = symbol },
        };
    }

    private static (LeverageTierRefresher Service, Mock<IExchangeConnectorFactory> Factory, Mock<IConnectorLifecycleManager> Lifecycle, Mock<IFundingRateRepository> Rates)
        BuildSut(IEnumerable<FundingRateSnapshot> rates)
    {
        var rateRepo = new Mock<IFundingRateRepository>();
        rateRepo.Setup(r => r.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates.ToList());

        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.FundingRates).Returns(rateRepo.Object);

        var factory = new Mock<IExchangeConnectorFactory>();
        var lifecycle = new Mock<IConnectorLifecycleManager>();

        var services = new ServiceCollection();
        services.AddSingleton(uow.Object);
        services.AddSingleton(factory.Object);
        services.AddSingleton(lifecycle.Object);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var refresher = new LeverageTierRefresher(
            scopeFactory,
            NullLogger<LeverageTierRefresher>.Instance,
            initialDelay: TimeSpan.FromMilliseconds(1),
            refreshInterval: TimeSpan.FromMilliseconds(50));

        return (refresher, factory, lifecycle, rateRepo);
    }

    [Fact]
    public async Task RefreshTiersAsync_NoLatestRates_LogsAndReturnsWithoutCallingFactory()
    {
        var (sut, factory, lifecycle, _) = BuildSut(Array.Empty<FundingRateSnapshot>());

        await sut.RefreshTiersAsync(CancellationToken.None);

        factory.Verify(f => f.GetConnector(It.IsAny<string>()), Times.Never);
        lifecycle.Verify(l => l.EnsureTiersCachedAsync(
            It.IsAny<IExchangeConnector>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshTiersAsync_FactoryThrowsForOneExchange_ContinuesWithRest()
    {
        var rates = new[]
        {
            MakeRate(1, "Hyperliquid", 1, "BTC"),
            MakeRate(2, "Lighter", 1, "BTC"),
        };

        var (sut, factory, lifecycle, _) = BuildSut(rates);

        var lighterConnector = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("Hyperliquid")).Throws(new InvalidOperationException("disabled"));
        factory.Setup(f => f.GetConnector("Lighter")).Returns(lighterConnector);

        await sut.RefreshTiersAsync(CancellationToken.None);

        // Lighter should still be cached even though Hyperliquid threw
        lifecycle.Verify(l => l.EnsureTiersCachedAsync(lighterConnector, "BTC", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RefreshTiersAsync_LifecycleThrowsForOneSymbol_ContinuesWithRest()
    {
        var rates = new[]
        {
            MakeRate(1, "Hyperliquid", 1, "BTC"),
            MakeRate(1, "Hyperliquid", 2, "ETH"),
        };

        var (sut, factory, lifecycle, _) = BuildSut(rates);
        var connector = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("Hyperliquid")).Returns(connector);

        // BTC throws, ETH succeeds
        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connector, "BTC", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("BTC tier fetch failed"));
        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connector, "ETH", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await sut.RefreshTiersAsync(CancellationToken.None);

        lifecycle.Verify(l => l.EnsureTiersCachedAsync(connector, "BTC", It.IsAny<CancellationToken>()), Times.Once);
        lifecycle.Verify(l => l.EnsureTiersCachedAsync(connector, "ETH", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RefreshTiersAsync_ConnectorCacheReused_GetConnectorCalledOncePerExchange()
    {
        // 5 different symbols on the same exchange — factory should be called once
        var rates = new[]
        {
            MakeRate(1, "Hyperliquid", 1, "BTC"),
            MakeRate(1, "Hyperliquid", 2, "ETH"),
            MakeRate(1, "Hyperliquid", 3, "SOL"),
            MakeRate(1, "Hyperliquid", 4, "AVAX"),
            MakeRate(1, "Hyperliquid", 5, "MATIC"),
        };

        var (sut, factory, lifecycle, _) = BuildSut(rates);
        var connector = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("Hyperliquid")).Returns(connector);

        await sut.RefreshTiersAsync(CancellationToken.None);

        factory.Verify(f => f.GetConnector("Hyperliquid"), Times.Once,
            "the connector cache should reuse the same instance across symbols");
        lifecycle.Verify(l => l.EnsureTiersCachedAsync(connector, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(5));
    }

    [Fact]
    public async Task RefreshTiersAsync_CancellationDuringPairLoop_PropagatesOperationCanceledException()
    {
        var rates = new[]
        {
            MakeRate(1, "Hyperliquid", 1, "BTC"),
            MakeRate(1, "Hyperliquid", 2, "ETH"),
        };

        var (sut, factory, lifecycle, _) = BuildSut(rates);
        var connector = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("Hyperliquid")).Returns(connector);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = async () => await sut.RefreshTiersAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExecuteAsync_CancellationDuringInitialDelay_ReturnsCleanly()
    {
        var rates = new[] { MakeRate(1, "Hyperliquid", 1, "BTC") };
        var rateRepo = new Mock<IFundingRateRepository>();
        rateRepo.Setup(r => r.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates.ToList());
        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.FundingRates).Returns(rateRepo.Object);

        var services = new ServiceCollection();
        services.AddSingleton(uow.Object);
        services.AddSingleton(new Mock<IExchangeConnectorFactory>().Object);
        services.AddSingleton(new Mock<IConnectorLifecycleManager>().Object);
        var provider = services.BuildServiceProvider();

        // Long initial delay, immediate cancellation
        var sut = new LeverageTierRefresher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<LeverageTierRefresher>.Instance,
            initialDelay: TimeSpan.FromSeconds(60),
            refreshInterval: TimeSpan.FromSeconds(60));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // StartAsync internally calls ExecuteAsync; should return cleanly without hanging
        await sut.StartAsync(cts.Token);
        await sut.StopAsync(CancellationToken.None);
    }
}
