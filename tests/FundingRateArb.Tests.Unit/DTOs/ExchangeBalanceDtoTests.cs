using FluentAssertions;
using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Tests.Unit.DTOs;

public class ExchangeBalanceDtoTests
{
    private static ExchangeBalanceDto StaleWithLastKnown(TimeSpan gapBeforeFetchedAt) =>
        new()
        {
            ExchangeId = 1,
            ExchangeName = "Test",
            AvailableUsdc = 0m,
            IsStale = true,
            FetchedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            LastKnownAvailableUsdc = 1000m,
            LastKnownAt = new DateTimeOffset(
                new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc) - gapBeforeFetchedAt,
                TimeSpan.Zero)
        };

    [Fact]
    public void IsFallbackEligible_WhenStaleAndLastKnownWithin5Min_ReturnsTrue()
    {
        var dto = StaleWithLastKnown(TimeSpan.FromMinutes(4));
        dto.IsFallbackEligible.Should().BeTrue();
    }

    [Fact]
    public void IsFallbackEligible_WhenStaleAndLastKnownOutside5Min_ReturnsFalse()
    {
        var dto = StaleWithLastKnown(TimeSpan.FromMinutes(6));
        dto.IsFallbackEligible.Should().BeFalse();
    }

    [Fact]
    public void IsFallbackEligible_WhenLastKnownAvailableUsdcNull_ReturnsFalse()
    {
        var dto = StaleWithLastKnown(TimeSpan.FromMinutes(4));
        dto.LastKnownAvailableUsdc = null;
        dto.IsFallbackEligible.Should().BeFalse();
    }

    [Fact]
    public void IsFallbackEligible_WhenNotStale_ReturnsFalse()
    {
        var dto = StaleWithLastKnown(TimeSpan.FromMinutes(4));
        dto.IsStale = false;
        dto.IsFallbackEligible.Should().BeFalse();
    }

    [Fact]
    public void DefaultConstruction_NewFieldsAreNull_AndSerializesWithoutError()
    {
        var dto = new ExchangeBalanceDto
        {
            ExchangeId = 1,
            ExchangeName = "Test",
            AvailableUsdc = 500m,
            FetchedAt = DateTime.UtcNow,
            IsStale = false,
            IsUnavailable = false
        };

        dto.LastKnownAvailableUsdc.Should().BeNull();
        dto.LastKnownAt.Should().BeNull();
        dto.IsFallbackEligible.Should().BeFalse();
    }
}
