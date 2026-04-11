using System;
using System.Threading;
using FluentAssertions;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Infrastructure.ExchangeConnectors;

namespace FundingRateArb.Tests.Unit.Infrastructure;

public class MarketDataCacheTests
{
    private readonly MarketDataCache _sut = new();

    private static FundingRateDto MakeDto(string exchange = "Aster", string symbol = "BTC",
        decimal rate = 0.0005m, decimal markPrice = 50000m, decimal volume = 1_000_000m) => new()
        {
            ExchangeName = exchange,
            Symbol = symbol,
            RatePerHour = rate,
            RawRate = rate,
            MarkPrice = markPrice,
            IndexPrice = markPrice,
            Volume24hUsd = volume,
        };

    [Fact]
    public void Update_StoresRate_RetrievableByExchangeAndSymbol()
    {
        var dto = MakeDto();
        _sut.Update(dto);

        var result = _sut.GetLatest("Aster", "BTC");
        result.Should().NotBeNull();
        result!.MarkPrice.Should().Be(50000m);
    }

    [Fact]
    public void Update_OverwritesPreviousRate_ForSameKey()
    {
        _sut.Update(MakeDto(markPrice: 50000m));
        _sut.Update(MakeDto(markPrice: 51000m));

        _sut.GetLatest("Aster", "BTC")!.MarkPrice.Should().Be(51000m);
    }

    [Fact]
    public void GetAllLatest_ReturnsAllCachedRates()
    {
        _sut.Update(MakeDto("Aster", "BTC"));
        _sut.Update(MakeDto("Hyperliquid", "ETH"));
        _sut.Update(MakeDto("Lighter", "SOL"));

        _sut.GetAllLatest().Should().HaveCount(3);
    }

    [Fact]
    public void GetMarkPrice_ReturnsZero_ForUnknownKey()
    {
        _sut.GetMarkPrice("Unknown", "BTC").Should().Be(0m);
    }

    [Fact]
    public void GetAllForExchange_ReturnsOnlyMatchingExchange()
    {
        _sut.Update(MakeDto("Aster", "BTC"));
        _sut.Update(MakeDto("Aster", "ETH"));
        _sut.Update(MakeDto("Lighter", "BTC"));

        var result = _sut.GetAllForExchange("Aster");
        result.Should().HaveCount(2);
        result.Should().OnlyContain(r => r.ExchangeName == "Aster");
    }

    [Fact]
    public void IsStale_ReturnsTrue_WhenRateOlderThanMaxAge()
    {
        _sut.Update(MakeDto());

        // With a zero-second max age, any update is immediately stale
        _sut.IsStale("Aster", "BTC", TimeSpan.Zero).Should().BeTrue();
    }

    [Fact]
    public void IsStale_ReturnsFalse_WhenRateFresh()
    {
        _sut.Update(MakeDto());

        _sut.IsStale("Aster", "BTC", TimeSpan.FromMinutes(5)).Should().BeFalse();
    }

    [Fact]
    public void IsStale_ReturnsTrue_WhenKeyNotFound()
    {
        _sut.IsStale("Unknown", "BTC", TimeSpan.FromMinutes(5)).Should().BeTrue();
    }

    [Fact]
    public void IsStaleForExchange_ReturnsTrue_WhenNoRatesExist()
    {
        _sut.IsStaleForExchange("Aster", TimeSpan.FromMinutes(5)).Should().BeTrue();
    }

    [Fact]
    public void GetLatest_ReturnsNull_ForUnknownKey()
    {
        _sut.GetLatest("Unknown", "BTC").Should().BeNull();
    }

    [Fact]
    public void GetMarkPrice_ReturnsCachedPrice()
    {
        _sut.Update(MakeDto(markPrice: 42000m));
        _sut.GetMarkPrice("Aster", "BTC").Should().Be(42000m);
    }

    [Fact]
    public void GetAllForExchange_CaseInsensitive()
    {
        _sut.Update(MakeDto("Aster", "BTC"));
        _sut.GetAllForExchange("aster").Should().HaveCount(1);
    }

    [Fact]
    public void Update_PreservesVolume_WhenNewVolumeIsZero()
    {
        _sut.Update(MakeDto("Aster", "BTC", volume: 1_000_000m));
        _sut.Update(new FundingRateDto
        {
            ExchangeName = "Aster",
            Symbol = "BTC",
            RatePerHour = 0.001m,
            RawRate = 0.001m,
            MarkPrice = 52000m,
            IndexPrice = 52000m,
            Volume24hUsd = 0m,
        });

        _sut.GetLatest("Aster", "BTC")!.Volume24hUsd.Should().Be(1_000_000m);
    }

    [Fact]
    public void Update_OverwritesVolume_WhenNewVolumeIsPositive()
    {
        _sut.Update(MakeDto("Aster", "BTC", volume: 1_000_000m));
        _sut.Update(MakeDto("Aster", "BTC", volume: 2_000_000m));

        _sut.GetLatest("Aster", "BTC")!.Volume24hUsd.Should().Be(2_000_000m);
    }

    [Fact]
    public void Update_AllowsZeroVolume_WhenNoPreviousEntry()
    {
        _sut.Update(new FundingRateDto
        {
            ExchangeName = "Aster",
            Symbol = "NEW",
            RatePerHour = 0.001m,
            RawRate = 0.001m,
            MarkPrice = 100m,
            IndexPrice = 100m,
            Volume24hUsd = 0m,
        });

        _sut.GetLatest("Aster", "NEW")!.Volume24hUsd.Should().Be(0m);
    }

    [Fact]
    public void GetLastFetchTime_ReturnsNull_WhenCacheEmpty()
    {
        _sut.GetLastFetchTime().Should().BeNull();
    }

    [Fact]
    public void GetLastFetchTime_ReturnsMaxTimestamp_AfterUpdates()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        _sut.Update(MakeDto("Aster", "BTC"));
        _sut.Update(MakeDto("Hyperliquid", "ETH"));
        var after = DateTime.UtcNow.AddSeconds(1);

        var result = _sut.GetLastFetchTime();

        result.Should().NotBeNull();
        result!.Value.Should().BeOnOrAfter(before);
        result!.Value.Should().BeOnOrBefore(after);
    }

    [Fact]
    public void GetLastFetchTime_ReturnsLatestTimestamp_WhenEntryUpdatedAfterOlderEntries()
    {
        // Seed with an initial entry, pause, then update a different key — the returned
        // max must reflect the later update, not the earlier one. A min-implementation
        // bug would return the older timestamp and fail the assertion.
        _sut.Update(MakeDto("Aster", "BTC"));
        var firstUpdateFinishedAt = DateTime.UtcNow;

        // Ensure a measurable gap between the two updates so max vs min is distinguishable.
        Thread.Sleep(50);

        _sut.Update(MakeDto("Hyperliquid", "ETH"));
        var secondUpdateStartedAt = DateTime.UtcNow.AddMilliseconds(-10);

        var result = _sut.GetLastFetchTime();

        result.Should().NotBeNull();
        result!.Value.Should().BeOnOrAfter(secondUpdateStartedAt,
            "GetLastFetchTime must return the latest (max) timestamp across all entries, not an older one");
        result.Value.Should().BeAfter(firstUpdateFinishedAt,
            "the max timestamp must be strictly newer than the first update's completion time");
    }
}
