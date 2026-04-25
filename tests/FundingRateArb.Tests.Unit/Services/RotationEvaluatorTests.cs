using FluentAssertions;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FundingRateArb.Tests.Unit.Services;

public class RotationEvaluatorTests
{
    private readonly RotationEvaluator _sut = new(NullLogger<RotationEvaluator>.Instance);

    private static ArbitragePosition MakePosition(
        int id = 1,
        int assetId = 1,
        int longExId = 1,
        int shortExId = 2,
        decimal currentSpread = 0.0001m,
        DateTime? openedAt = null,
        PositionStatus status = PositionStatus.Open)
    {
        return new ArbitragePosition
        {
            Id = id,
            AssetId = assetId,
            LongExchangeId = longExId,
            ShortExchangeId = shortExId,
            CurrentSpreadPerHour = currentSpread,
            OpenedAt = openedAt ?? DateTime.UtcNow.AddMinutes(-60),
            Status = status,
            Asset = new Asset { Id = assetId, Symbol = $"ASSET{assetId}" },
            LongExchange = new Exchange { Id = longExId, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = shortExId, Name = "Lighter" },
        };
    }

    private static ArbitrageOpportunityDto MakeOpportunity(
        int assetId = 2,
        int longExId = 1,
        int shortExId = 3,
        decimal netYield = 0.0005m,
        string asset = "BTC",
        string longExName = "Hyperliquid",
        string shortExName = "Aster")
    {
        return new ArbitrageOpportunityDto
        {
            AssetSymbol = asset,
            AssetId = assetId,
            LongExchangeName = longExName,
            LongExchangeId = longExId,
            ShortExchangeName = shortExName,
            ShortExchangeId = shortExId,
            NetYieldPerHour = netYield,
            SpreadPerHour = netYield,
            LongVolume24h = 1_000_000m,
            ShortVolume24h = 1_000_000m,
        };
    }

    private static UserConfiguration MakeUserConfig(
        decimal rotationThreshold = 0.0003m,
        int minHoldMinutes = 30,
        int maxRotationsPerDay = 5)
    {
        return new UserConfiguration
        {
            RotationThresholdPerHour = rotationThreshold,
            MinHoldBeforeRotationMinutes = minHoldMinutes,
            MaxRotationsPerDay = maxRotationsPerDay,
        };
    }

    private static BotConfiguration MakeGlobalConfig(decimal closeThreshold = -0.00005m)
    {
        return new BotConfiguration
        {
            CloseThreshold = closeThreshold,
            UpdatedByUserId = "admin",
        };
    }

    [Fact]
    public void Evaluate_SpreadImprovementExceedsThreshold_AndHoldTimeMet_ReturnsRecommendation()
    {
        // Position: spread=0.0001/hr, opened 60 min ago
        var position = MakePosition(id: 1, currentSpread: 0.0001m, openedAt: DateTime.UtcNow.AddMinutes(-60));
        // Best opportunity: yield=0.0005/hr
        var opportunity = MakeOpportunity(netYield: 0.0005m);
        var userConfig = MakeUserConfig(rotationThreshold: 0.0003m, minHoldMinutes: 30);
        var globalConfig = MakeGlobalConfig();

        var result = _sut.Evaluate([position], [opportunity], userConfig, globalConfig);

        // Improvement = 0.0005 - 0.0001 = 0.0004 > threshold 0.0003
        // Hold time: 60 > 30 min
        result.Should().NotBeNull();
        result!.PositionId.Should().Be(1);
        result.ImprovementPerHour.Should().Be(0.0004m);
        result.ReplacementNetYieldPerHour.Should().Be(0.0005m);
        result.CurrentSpreadPerHour.Should().Be(0.0001m);
    }

    [Fact]
    public void Evaluate_SpreadImprovementBelowThreshold_ReturnsNull()
    {
        // Position: spread=0.0003/hr, opened 60 min ago
        var position = MakePosition(id: 1, currentSpread: 0.0003m, openedAt: DateTime.UtcNow.AddMinutes(-60));
        // Best opportunity: yield=0.0005/hr
        var opportunity = MakeOpportunity(netYield: 0.0005m);
        var userConfig = MakeUserConfig(rotationThreshold: 0.0003m, minHoldMinutes: 30);
        var globalConfig = MakeGlobalConfig();

        var result = _sut.Evaluate([position], [opportunity], userConfig, globalConfig);

        // Improvement = 0.0005 - 0.0003 = 0.0002 <= threshold 0.0003
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_CurrentSpreadBelowCloseThreshold_RotatesImmediately()
    {
        // Position: spread=-0.0001/hr (below CloseThreshold=-0.00005), opened 5 min ago
        var position = MakePosition(id: 1, currentSpread: -0.0001m, openedAt: DateTime.UtcNow.AddMinutes(-5));
        // Best opportunity: yield=0.0004/hr
        var opportunity = MakeOpportunity(netYield: 0.0004m);
        var userConfig = MakeUserConfig(rotationThreshold: 0.0003m, minHoldMinutes: 30);
        var globalConfig = MakeGlobalConfig(closeThreshold: -0.00005m);

        var result = _sut.Evaluate([position], [opportunity], userConfig, globalConfig);

        // Improvement = 0.0004 - (-0.0001) = 0.0005 > threshold 0.0003
        // Hold time: 5 min < 30 min BUT spread < CloseThreshold so hold time skipped
        result.Should().NotBeNull();
        result!.PositionId.Should().Be(1);
        result.ImprovementPerHour.Should().Be(0.0005m);
    }

    [Fact]
    public void Evaluate_HoldTimeNotMet_ReturnsNull()
    {
        // Position: spread=0.0001/hr (above CloseThreshold), opened 10 min ago
        var position = MakePosition(id: 1, currentSpread: 0.0001m, openedAt: DateTime.UtcNow.AddMinutes(-10));
        // Best opportunity: yield=0.0005/hr
        var opportunity = MakeOpportunity(netYield: 0.0005m);
        var userConfig = MakeUserConfig(rotationThreshold: 0.0003m, minHoldMinutes: 30);
        var globalConfig = MakeGlobalConfig();

        var result = _sut.Evaluate([position], [opportunity], userConfig, globalConfig);

        // Improvement = 0.0004 > threshold, but hold time 10 < 30 min
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_NoAvailableOpportunities_ReturnsNull()
    {
        // Position occupies (assetId=1, longExId=1, shortExId=2)
        var position = MakePosition(id: 1, assetId: 1, longExId: 1, shortExId: 2);
        // Only opportunity is the same combo — gets excluded
        var opportunity = MakeOpportunity(assetId: 1, longExId: 1, shortExId: 2, netYield: 0.001m);
        var userConfig = MakeUserConfig();
        var globalConfig = MakeGlobalConfig();

        var result = _sut.Evaluate([position], [opportunity], userConfig, globalConfig);

        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_OccupiedPositionsExcludedFromOpportunities()
    {
        // Two positions occupy two different opportunity combos
        var pos1 = MakePosition(id: 1, assetId: 1, longExId: 1, shortExId: 2, currentSpread: 0.0001m);
        var pos2 = MakePosition(id: 2, assetId: 2, longExId: 1, shortExId: 3, currentSpread: 0.0002m);

        // Three opportunities: first two match occupied positions, third is available
        var opp1 = MakeOpportunity(assetId: 1, longExId: 1, shortExId: 2, netYield: 0.001m); // occupied
        var opp2 = MakeOpportunity(assetId: 2, longExId: 1, shortExId: 3, netYield: 0.001m); // occupied
        var opp3 = MakeOpportunity(assetId: 3, longExId: 1, shortExId: 3, netYield: 0.0008m, asset: "SOL"); // available

        var userConfig = MakeUserConfig(rotationThreshold: 0.0003m, minHoldMinutes: 30);
        var globalConfig = MakeGlobalConfig();

        var result = _sut.Evaluate([pos1, pos2], [opp1, opp2, opp3], userConfig, globalConfig);

        // Should target worst position (pos1, spread=0.0001) and replacement opp3 (yield=0.0008)
        result.Should().NotBeNull();
        result!.PositionId.Should().Be(1);
        result.ReplacementAsset.Should().Be("SOL");
        result.ReplacementAssetId.Should().Be(3);
        result.ImprovementPerHour.Should().Be(0.0008m - 0.0001m);
    }

    [Fact]
    public void Evaluate_SpreadImprovementEqualsThreshold_ReturnsNull()
    {
        // Position: spread=0.0002/hr, opened 60 min ago
        var position = MakePosition(id: 1, currentSpread: 0.0002m, openedAt: DateTime.UtcNow.AddMinutes(-60));
        // Best opportunity: yield=0.0005/hr → improvement = 0.0003 == threshold
        var opportunity = MakeOpportunity(netYield: 0.0005m);
        var userConfig = MakeUserConfig(rotationThreshold: 0.0003m, minHoldMinutes: 30);
        var globalConfig = MakeGlobalConfig();

        var result = _sut.Evaluate([position], [opportunity], userConfig, globalConfig);

        // Improvement exactly equals threshold — should NOT rotate (uses <=)
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_EmptyPositions_ReturnsNull()
    {
        var opportunity = MakeOpportunity(netYield: 0.001m);
        var userConfig = MakeUserConfig();
        var globalConfig = MakeGlobalConfig();

        var result = _sut.Evaluate([], [opportunity], userConfig, globalConfig);

        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_EmptyOpportunities_ReturnsNull()
    {
        var position = MakePosition(id: 1, currentSpread: 0.0001m);
        var userConfig = MakeUserConfig();
        var globalConfig = MakeGlobalConfig();

        var result = _sut.Evaluate([position], [], userConfig, globalConfig);

        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_NonOpenPosition_Excluded()
    {
        // Only position has Opening status — should be excluded from evaluation
        var position = MakePosition(id: 1, currentSpread: 0.0001m, status: PositionStatus.Opening);
        var opportunity = MakeOpportunity(netYield: 0.001m);
        var userConfig = MakeUserConfig();
        var globalConfig = MakeGlobalConfig();

        var result = _sut.Evaluate([position], [opportunity], userConfig, globalConfig);

        result.Should().BeNull();
    }

    // ── Divergence exit cost gate (Task 2.2) ─────────────────────────────────

    [Fact]
    public void Evaluate_DivergenceExitCostExceedsNewEdge_ReturnsNull()
    {
        // worst position: divergence=2%, size=10_000 → exit cost = 200
        // best opportunity: netYield=0.001/hr, horizon=2h → edge = 0.001 * 2 * 10_000 = 20
        // 200 > 20 → suppressed → null
        var position = MakePosition(id: 1, currentSpread: 0.0001m);
        position.SizeUsdc = 10_000m;
        position.CurrentDivergencePct = 2.0m;

        var opportunity = MakeOpportunity(netYield: 0.001m);
        var userConfig = MakeUserConfig(rotationThreshold: 0.0003m, minHoldMinutes: 0);

        var globalConfig = new BotConfiguration
        {
            CloseThreshold = -0.00005m,
            UpdatedByUserId = "admin",
            RotationDivergenceHorizonHours = 2.0m,
        };

        var result = _sut.Evaluate([position], [opportunity], userConfig, globalConfig);

        result.Should().BeNull("divergence exit cost 200 exceeds new edge 20 over 2h");
    }

    [Fact]
    public void Evaluate_DivergenceExitCostBelowNewEdge_ReturnsRecommendation()
    {
        // worst position: divergence=0.05%, size=10_000 → exit cost = 5
        // best opportunity: netYield=0.001/hr, horizon=2h → edge = 20
        // 5 < 20 → allowed → recommendation returned
        var position = MakePosition(id: 1, currentSpread: 0.0001m);
        position.SizeUsdc = 10_000m;
        position.CurrentDivergencePct = 0.05m;

        var opportunity = MakeOpportunity(netYield: 0.001m);
        var userConfig = MakeUserConfig(rotationThreshold: 0.0003m, minHoldMinutes: 0);

        var globalConfig = new BotConfiguration
        {
            CloseThreshold = -0.00005m,
            UpdatedByUserId = "admin",
            RotationDivergenceHorizonHours = 2.0m,
        };

        var result = _sut.Evaluate([position], [opportunity], userConfig, globalConfig);

        result.Should().NotBeNull("divergence exit cost 5 is below new edge 20 — rotation should proceed");
    }

    [Fact]
    public void Evaluate_NullDivergence_TreatsAsZeroCost_ReturnsRecommendation()
    {
        // null CurrentDivergencePct → exit cost = 0 → always passes the gate
        var position = MakePosition(id: 1, currentSpread: 0.0001m);
        position.SizeUsdc = 10_000m;
        position.CurrentDivergencePct = null;

        var opportunity = MakeOpportunity(netYield: 0.001m);
        var userConfig = MakeUserConfig(rotationThreshold: 0.0003m, minHoldMinutes: 0);

        var globalConfig = new BotConfiguration
        {
            CloseThreshold = -0.00005m,
            UpdatedByUserId = "admin",
            RotationDivergenceHorizonHours = 2.0m,
        };

        var result = _sut.Evaluate([position], [opportunity], userConfig, globalConfig);

        result.Should().NotBeNull("null divergence treated as zero exit cost — should not suppress rotation");
    }
}
