using FluentAssertions;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Services;

namespace FundingRateArb.Tests.Unit.Services;

public class SkipReasonTrackerTests
{
    [Fact]
    public void FundingDeviationWindowKeys_IsInitiallyEmpty()
    {
        var tracker = new SkipReasonTracker();

        tracker.FundingDeviationWindowKeys.Should().BeEmpty();
    }

    [Fact]
    public void ClearPerUserSets_ClearsFundingDeviationWindowKeys()
    {
        var tracker = new SkipReasonTracker();
        tracker.FundingDeviationWindowKeys.Add("key:1");

        tracker.ClearPerUserSets();

        tracker.FundingDeviationWindowKeys.Should().BeEmpty();
    }

    [Fact]
    public void ClearPerUserSets_PreservesGlobalSets()
    {
        var tracker = new SkipReasonTracker();
        tracker.OpenedOppKeys.Add("opened:1");
        tracker.FundingDeviationWindowKeys.Add("dev:1");

        tracker.ClearPerUserSets();

        tracker.OpenedOppKeys.Should().Contain("opened:1",
            "global set OpenedOppKeys must not be cleared by ClearPerUserSets");
        tracker.FundingDeviationWindowKeys.Should().BeEmpty();
    }

    [Fact]
    public void PairsFilteredByTrendUnconfirmed_StartsAtZero_AndIncrements_WhenRecorded()
    {
        var diagnostics = new PipelineDiagnosticsDto();

        diagnostics.PairsFilteredByTrendUnconfirmed.Should().Be(0,
            "counter must start at zero before any evaluation");

        diagnostics.PairsFilteredByTrendUnconfirmed++;

        diagnostics.PairsFilteredByTrendUnconfirmed.Should().Be(1,
            "counter must increment when a trend-unconfirmed pair is recorded");
    }
}
