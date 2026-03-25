using FluentAssertions;
using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Tests.Integration.Repositories;

public class OpportunitySnapshotRepositoryTests : IDisposable
{
    private readonly TestDbFixture _fixture;

    public OpportunitySnapshotRepositoryTests()
    {
        _fixture = new TestDbFixture();
    }

    private OpportunitySnapshot BuildSnapshot(bool wasOpened, string? skipReason = null, DateTime? recordedAt = null) => new()
    {
        AssetId = _fixture.TestAsset.Id,
        LongExchangeId = _fixture.TestExchange.Id,
        ShortExchangeId = _fixture.TestExchange.Id,
        SpreadPerHour = 0.001m,
        NetYieldPerHour = 0.0008m,
        LongVolume24h = 1_000_000m,
        ShortVolume24h = 500_000m,
        WasOpened = wasOpened,
        SkipReason = skipReason,
        RecordedAt = recordedAt ?? DateTime.UtcNow,
    };

    [Fact]
    public async Task GetSkipReasonStatsAsync_ReturnsCorrectCounts()
    {
        // Arrange — seed snapshots with various states
        var snapshots = new[]
        {
            BuildSnapshot(wasOpened: true),
            BuildSnapshot(wasOpened: true),
            BuildSnapshot(wasOpened: false, skipReason: "capital_exhausted"),
            BuildSnapshot(wasOpened: false, skipReason: "capital_exhausted"),
            BuildSnapshot(wasOpened: false, skipReason: "capital_exhausted"),
            BuildSnapshot(wasOpened: false, skipReason: "cooldown"),
            BuildSnapshot(wasOpened: false, skipReason: "below_threshold"),
        };

        await _fixture.UnitOfWork.OpportunitySnapshots.AddRangeAsync(snapshots);
        await _fixture.UnitOfWork.SaveAsync();

        // Act
        var (total, opened, reasons) = await _fixture.UnitOfWork.OpportunitySnapshots
            .GetSkipReasonStatsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));

        // Assert
        total.Should().Be(7);
        opened.Should().Be(2);
        reasons.Should().HaveCount(3);
        reasons["capital_exhausted"].Should().Be(3);
        reasons["cooldown"].Should().Be(1);
        reasons["below_threshold"].Should().Be(1);
    }

    [Fact]
    public async Task GetSkipReasonStatsAsync_NullAndEmptySkipReasons_ExcludedFromDict()
    {
        // Arrange — snapshots with null and empty skip reasons
        var snapshots = new[]
        {
            BuildSnapshot(wasOpened: false, skipReason: null),
            BuildSnapshot(wasOpened: false, skipReason: ""),
            BuildSnapshot(wasOpened: false, skipReason: "cooldown"),
        };

        await _fixture.UnitOfWork.OpportunitySnapshots.AddRangeAsync(snapshots);
        await _fixture.UnitOfWork.SaveAsync();

        // Act
        var (total, opened, reasons) = await _fixture.UnitOfWork.OpportunitySnapshots
            .GetSkipReasonStatsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));

        // Assert — null and empty skip reasons excluded from dictionary
        total.Should().Be(3);
        opened.Should().Be(0);
        reasons.Should().HaveCount(1);
        reasons.Should().ContainKey("cooldown");
    }

    [Fact]
    public async Task GetSkipReasonStatsAsync_NoSnapshots_ReturnsZeros()
    {
        // Act — no data seeded
        var (total, opened, reasons) = await _fixture.UnitOfWork.OpportunitySnapshots
            .GetSkipReasonStatsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));

        // Assert
        total.Should().Be(0);
        opened.Should().Be(0);
        reasons.Should().BeEmpty();
    }

    public void Dispose() => _fixture.Dispose();
}
