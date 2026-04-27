using FluentAssertions;
using FundingRateArb.Domain.Entities;
using Xunit;

namespace FundingRateArb.Tests.Unit.Domain;

public class ReconciliationReportTests
{
    [Fact]
    public void DefaultConstructor_OverallStatus_IsHealthy()
    {
        new ReconciliationReport().OverallStatus.Should().Be("Healthy");
    }

    [Fact]
    public void DefaultConstructor_AllCounters_AreZero()
    {
        var report = new ReconciliationReport();
        report.FreshRateMismatchCount.Should().Be(0);
        report.OrphanPositionCount.Should().Be(0);
        report.PhantomFeeRowCount24h.Should().Be(0);
        report.FeeDeltaOutsideToleranceCount.Should().Be(0);
        report.PerExchangeEquityJson.Should().BeEmpty();
        report.DegradedExchangesJson.Should().BeEmpty();
        report.AnomalySummary.Should().BeNullOrEmpty();
    }
}
