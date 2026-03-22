using FluentAssertions;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Tests.Unit.Services;

public class PortfolioRebalancerTests
{
    private readonly PortfolioRebalancer _sut = new();

    private static BotConfiguration MakeConfig(
        bool rebalanceEnabled = true,
        decimal rebalanceMinImprovement = 0.0002m,
        int maxHoldTimeHours = 72)
    {
        return new BotConfiguration
        {
            RebalanceEnabled = rebalanceEnabled,
            RebalanceMinImprovement = rebalanceMinImprovement,
            MaxHoldTimeHours = maxHoldTimeHours,
        };
    }

    private static ArbitragePosition MakePosition(
        int id = 1,
        decimal currentSpread = 0.0001m,
        decimal sizeUsdc = 1000m,
        double hoursAgo = 10,
        int leverage = 5)
    {
        return new ArbitragePosition
        {
            Id = id,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = sizeUsdc,
            Leverage = leverage,
            CurrentSpreadPerHour = currentSpread,
            OpenedAt = DateTime.UtcNow.AddHours(-hoursAgo),
            Asset = new Asset { Id = 1, Symbol = "ETH" },
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
        };
    }

    private static ArbitrageOpportunityDto MakeOpportunity(
        string asset = "BTC",
        int assetId = 2,
        decimal netYield = 0.001m,
        decimal spread = 0.0012m)
    {
        return new ArbitrageOpportunityDto
        {
            AssetSymbol = asset,
            AssetId = assetId,
            LongExchangeName = "Hyperliquid",
            LongExchangeId = 1,
            ShortExchangeName = "Aster",
            ShortExchangeId = 3,
            SpreadPerHour = spread,
            NetYieldPerHour = netYield,
            LongVolume24h = 1000000m,
            ShortVolume24h = 1000000m,
        };
    }

    [Fact]
    public async Task EvaluateAsync_LowSpreadPosition_HighSpreadOpportunity_RecommendsRebalance()
    {
        var pos = MakePosition(currentSpread: 0.00005m); // low spread
        var opp = MakeOpportunity(netYield: 0.001m);     // high yield
        var config = MakeConfig();

        var result = await _sut.EvaluateAsync([pos], [opp], config);

        result.Should().HaveCount(1);
        result[0].PositionId.Should().Be(1);
        result[0].ReplacementAsset.Should().Be("BTC");
        result[0].ExpectedImprovement.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task EvaluateAsync_HighSpreadPosition_NoBetterOpportunity_NoRecommendation()
    {
        var pos = MakePosition(currentSpread: 0.001m);                // high spread
        var opp = MakeOpportunity(netYield: 0.0001m, spread: 0.0001m); // low spread
        var config = MakeConfig();

        var result = await _sut.EvaluateAsync([pos], [opp], config);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateAsync_PositionOpenedLessThanOneHour_Skipped()
    {
        var pos = MakePosition(currentSpread: 0.00001m, hoursAgo: 0.5); // opened 30 min ago
        var opp = MakeOpportunity(netYield: 0.01m);  // way better
        var config = MakeConfig();

        var result = await _sut.EvaluateAsync([pos], [opp], config);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateAsync_RebalanceDisabled_ReturnsEmpty()
    {
        var pos = MakePosition(currentSpread: 0.00001m);
        var opp = MakeOpportunity(netYield: 0.01m);
        var config = MakeConfig(rebalanceEnabled: false);

        var result = await _sut.EvaluateAsync([pos], [opp], config);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateAsync_SwitchingCostTooHigh_NoRecommendation()
    {
        // Very small improvement that doesn't exceed minImprovement * sizeUsdc
        var pos = MakePosition(currentSpread: 0.0009m);
        var opp = MakeOpportunity(netYield: 0.00091m, spread: 0.00091m); // marginally better gross spread
        var config = MakeConfig(rebalanceMinImprovement: 0.01m); // high threshold

        var result = await _sut.EvaluateAsync([pos], [opp], config);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateAsync_TwoPositions_OneOpportunity_OnlyBestMatchRecommended()
    {
        var pos1 = MakePosition(id: 1, currentSpread: 0.00005m);
        var pos2 = MakePosition(id: 2, currentSpread: 0.00001m); // worse position
        pos2.AssetId = 3;
        pos2.Asset = new Asset { Id = 3, Symbol = "SOL" };
        pos2.LongExchangeId = 1;
        pos2.ShortExchangeId = 2;

        var opp = MakeOpportunity(netYield: 0.001m); // single good opportunity
        var config = MakeConfig();

        var result = await _sut.EvaluateAsync([pos1, pos2], [opp], config);

        // Only one recommendation since there's only one opportunity
        result.Should().HaveCount(1);
        // The worse position (pos2, lower current spread) should benefit most from rebalancing
        result[0].PositionId.Should().Be(2);
    }

    [Fact]
    public async Task EvaluateAsync_TwoOpportunities_TwoPositions_EachGetsDistinctOpportunity()
    {
        var pos1 = MakePosition(id: 1, currentSpread: 0.00005m);
        var pos2 = MakePosition(id: 2, currentSpread: 0.00003m);
        pos2.AssetId = 3;
        pos2.Asset = new Asset { Id = 3, Symbol = "SOL" };
        pos2.LongExchangeId = 1;
        pos2.ShortExchangeId = 2;

        var opp1 = MakeOpportunity(asset: "BTC", assetId: 2, netYield: 0.001m, spread: 0.0012m);
        var opp2 = MakeOpportunity(asset: "AVAX", assetId: 4, netYield: 0.0008m, spread: 0.001m);
        // Give opp2 different exchange combo
        opp2.LongExchangeId = 2;
        opp2.ShortExchangeId = 3;
        opp2.LongExchangeName = "Lighter";
        opp2.ShortExchangeName = "Aster";

        var config = MakeConfig();

        var result = await _sut.EvaluateAsync([pos1, pos2], [opp1, opp2], config);

        // Both positions should get distinct opportunities
        result.Should().HaveCount(2);
        var positionIds = result.Select(r => r.PositionId).ToHashSet();
        positionIds.Should().Contain(1);
        positionIds.Should().Contain(2);
        var replacementAssets = result.Select(r => r.ReplacementAsset).ToHashSet();
        replacementAssets.Should().HaveCount(2, "each position should get a different opportunity");
    }

    // ── NB5: MaxRebalancesPerCycle cap verification ──────────────

    [Fact]
    public async Task EvaluateAsync_ProducesMoreRecommendationsThanCap_OrchestratorMustCap()
    {
        // 3 positions with low spreads, 3 distinct high-yield opportunities
        // Rebalancer should recommend all 3; BotOrchestrator caps at MaxRebalancesPerCycle (default 2)
        var pos1 = MakePosition(id: 1, currentSpread: 0.00001m);
        var pos2 = MakePosition(id: 2, currentSpread: 0.00002m);
        pos2.AssetId = 2; pos2.Asset = new Asset { Id = 2, Symbol = "BTC" };
        pos2.LongExchangeId = 1; pos2.ShortExchangeId = 2;
        var pos3 = MakePosition(id: 3, currentSpread: 0.00003m);
        pos3.AssetId = 3; pos3.Asset = new Asset { Id = 3, Symbol = "SOL" };
        pos3.LongExchangeId = 1; pos3.ShortExchangeId = 2;

        var opp1 = MakeOpportunity(asset: "AVAX", assetId: 4, netYield: 0.001m);
        var opp2 = MakeOpportunity(asset: "DOGE", assetId: 5, netYield: 0.0009m);
        opp2.LongExchangeId = 2; opp2.ShortExchangeId = 3;
        opp2.LongExchangeName = "Lighter"; opp2.ShortExchangeName = "Aster";
        var opp3 = MakeOpportunity(asset: "LINK", assetId: 6, netYield: 0.0008m);
        opp3.LongExchangeId = 3; opp3.ShortExchangeId = 1;
        opp3.LongExchangeName = "Aster"; opp3.ShortExchangeName = "Hyperliquid";

        var config = MakeConfig();

        var result = await _sut.EvaluateAsync([pos1, pos2, pos3], [opp1, opp2, opp3], config);

        // Rebalancer returns all valid recommendations (> 2, which is the default cap)
        result.Should().HaveCountGreaterOrEqualTo(3,
            "rebalancer should return all recommendations; orchestrator applies MaxRebalancesPerCycle cap");
    }

    // ── NB2: Null navigation property guard ──────────────────────

    [Fact]
    public async Task EvaluateAsync_NullLongExchange_SkipsPosition()
    {
        var pos = MakePosition(currentSpread: 0.00001m);
        pos.LongExchange = null!; // simulate unloaded nav property
        var opp = MakeOpportunity(netYield: 0.01m);
        var config = MakeConfig();

        var result = await _sut.EvaluateAsync([pos], [opp], config);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateAsync_NullShortExchange_SkipsPosition()
    {
        var pos = MakePosition(currentSpread: 0.00001m);
        pos.ShortExchange = null!; // simulate unloaded nav property
        var opp = MakeOpportunity(netYield: 0.01m);
        var config = MakeConfig();

        var result = await _sut.EvaluateAsync([pos], [opp], config);

        result.Should().BeEmpty();
    }

    // ── F9: Edge case tests ───────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_PositionPastMaxHoldTime_Skipped()
    {
        // Position open longer than MaxHoldTimeHours → remainingHours <= 0 → skip
        var pos = MakePosition(currentSpread: 0.00001m, hoursAgo: 80); // 80h open, max is 72h
        var opp = MakeOpportunity(netYield: 0.01m);
        var config = MakeConfig(maxHoldTimeHours: 72);

        var result = await _sut.EvaluateAsync([pos], [opp], config);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateAsync_OpportunityMatchesHeldPosition_Skipped()
    {
        // Opportunity has same asset/exchange combo as an open position
        var pos = MakePosition(currentSpread: 0.00001m);
        // Match the opportunity to the same combo (assetId=1, longExchangeId=1, shortExchangeId=2)
        var opp = new ArbitrageOpportunityDto
        {
            AssetSymbol = "ETH",
            AssetId = 1,
            LongExchangeName = "Hyperliquid",
            LongExchangeId = 1,
            ShortExchangeName = "Lighter",
            ShortExchangeId = 2,
            SpreadPerHour = 0.01m,
            NetYieldPerHour = 0.009m,
            LongVolume24h = 1000000m,
            ShortVolume24h = 1000000m,
        };
        var config = MakeConfig();

        var result = await _sut.EvaluateAsync([pos], [opp], config);

        result.Should().BeEmpty();
    }
}
