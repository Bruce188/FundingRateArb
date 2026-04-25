using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class CapitalProviderTests
{
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IBotConfigRepository> _botConfig = new();
    private readonly Mock<IFundingRateRepository> _fundingRates = new();
    private readonly Mock<IUserConfigurationRepository> _userConfigs = new();
    private readonly Mock<IBalanceAggregator> _balanceAggregator = new();

    public CapitalProviderTests()
    {
        _uow.Setup(u => u.BotConfig).Returns(_botConfig.Object);
        _uow.Setup(u => u.FundingRates).Returns(_fundingRates.Object);
        _uow.Setup(u => u.UserConfigurations).Returns(_userConfigs.Object);
        _userConfigs.Setup(r => r.GetAllEnabledUserIdsAsync())
            .ReturnsAsync(new List<string> { "user1" });
    }

    private CapitalProvider BuildSut(IMemoryCache? cache = null) =>
        new(_uow.Object, _balanceAggregator.Object, cache ?? new MemoryCache(new MemoryCacheOptions()));

    private void SetupConfig(decimal totalCapitalUsdc)
    {
#pragma warning disable CS0618
        _botConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { TotalCapitalUsdc = totalCapitalUsdc });
#pragma warning restore CS0618
    }

    private void SetupBalance(decimal availableUsdc) =>
        _balanceAggregator
            .Setup(a => a.GetBalanceSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto
            {
                Balances = [new ExchangeBalanceDto { ExchangeId = 1, ExchangeName = "TestEx", AvailableUsdc = availableUsdc }]
            });

    // AC: live=$56 cap=$500 → result is $56 (live is smaller)
    [Fact]
    public async Task LiveBelowCap_ReturnsLiveBalance()
    {
        SetupConfig(500m);
        SetupBalance(56m);
        var sut = BuildSut();

        var result = await sut.GetEvaluatedCapitalUsdcAsync();

        result.Should().Be(56m);
    }

    // AC: live=$2000 cap=$500 → result is $500 (cap is smaller)
    [Fact]
    public async Task LiveAboveCap_ReturnsCap()
    {
        SetupConfig(500m);
        SetupBalance(2_000m);
        var sut = BuildSut();

        var result = await sut.GetEvaluatedCapitalUsdcAsync();

        result.Should().Be(500m);
    }

    // AC: two calls within 30 s → IBalanceAggregator called only once
    [Fact]
    public async Task CacheHit_BalanceAggregatorCalledOnce()
    {
        SetupConfig(500m);
        SetupBalance(56m);
        var sut = BuildSut();

        await sut.GetEvaluatedCapitalUsdcAsync();
        await sut.GetEvaluatedCapitalUsdcAsync();

        _balanceAggregator.Verify(
            a => a.GetBalanceSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // AC: cache entry manually expired → second call hits IBalanceAggregator again
    [Fact]
    public async Task AfterCacheExpiry_BalanceAggregatorCalledAgain()
    {
        SetupConfig(500m);
        SetupBalance(56m);

        // Use a real MemoryCache with a very short TTL by pre-populating an expired entry.
        // We do this by calling once, then evicting the key manually, then calling again.
        var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = BuildSut(cache);

        await sut.GetEvaluatedCapitalUsdcAsync(); // first call — populates cache
        cache.Remove("cp:evaluated-capital:v1");  // simulate expiry
        await sut.GetEvaluatedCapitalUsdcAsync(); // second call — must re-fetch

        _balanceAggregator.Verify(
            a => a.GetBalanceSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // AC: IBalanceAggregator throws → falls back to config.TotalCapitalUsdc, engine keeps running
    [Fact]
    public async Task AggregatorThrows_FallsBackToConfigCap()
    {
        SetupConfig(500m);
        _balanceAggregator
            .Setup(a => a.GetBalanceSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("exchange unavailable"));
        var sut = BuildSut();

        var result = await sut.GetEvaluatedCapitalUsdcAsync();

        result.Should().Be(500m, "fallback must return config cap when aggregator is unavailable");
    }
}
