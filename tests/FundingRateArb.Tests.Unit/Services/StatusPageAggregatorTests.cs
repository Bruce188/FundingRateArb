using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FundingRateArb.Application.Common;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Services;
using FundingRateArb.Application.ViewModels;
using FundingRateArb.Domain.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace FundingRateArb.Tests.Unit.Services;

public class StatusPageAggregatorTests
{
    private static (StatusPageAggregator sut, Mock<IUnitOfWork> mockUow, IMemoryCache cache)
        CreateSut(Action<Mock<IUnitOfWork>>? configureUow = null)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var mockUow = new Mock<IUnitOfWork>();
        var mockBotConfig = new Mock<IBotConfigRepository>();
        var mockPositions = new Mock<IPositionRepository>();
        var mockReconRepo = new Mock<IReconciliationReportRepository>();
        var mockExchanges = new Mock<IExchangeRepository>();

        // Default bot configuration
        mockBotConfig.Setup(r => r.GetActiveAsync()).ReturnsAsync(new BotConfiguration
        {
            IsEnabled = true,
            UpdatedByUserId = "admin",
        });

        // Default: empty repo responses
        mockPositions.Setup(r => r.GetPnlAttributionWindowsAsync(It.IsAny<IReadOnlyList<DateTime>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PnlAttributionWindowDto>());
        mockPositions.Setup(r => r.GetHoldTimeBucketsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HoldTimeBucketDto>());
        mockPositions.Setup(r => r.CountEmergencyClosedZeroFillSinceAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        mockPositions.Setup(r => r.CountPhantomFeeRowsSinceAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        mockPositions.Setup(r => r.GetPerExchangePairKpiAsync(It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExchangePairKpiAggregateDto>());
        mockPositions.Setup(r => r.GetPerAssetKpiAsync(It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AssetKpiAggregateDto>());
        mockPositions.Setup(r => r.GetRecentFailedOpensAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FailedOpenEventDto>());
        mockExchanges.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Exchange>());
        mockReconRepo.Setup(r => r.GetMostRecentAsync(It.IsAny<CancellationToken>())).ReturnsAsync((ReconciliationReport?)null);

        mockUow.Setup(u => u.BotConfig).Returns(mockBotConfig.Object);
        mockUow.Setup(u => u.Positions).Returns(mockPositions.Object);
        mockUow.Setup(u => u.ReconciliationReports).Returns(mockReconRepo.Object);
        mockUow.Setup(u => u.Exchanges).Returns(mockExchanges.Object);

        configureUow?.Invoke(mockUow);

        var scope = new Mock<IServiceScope>();
        var sp = new Mock<IServiceProvider>();
        sp.Setup(p => p.GetService(typeof(IUnitOfWork))).Returns(mockUow.Object);
        scope.SetupGet(s => s.ServiceProvider).Returns(sp.Object);
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var logger = new Mock<ILogger<StatusPageAggregator>>();

        var sut = new StatusPageAggregator(cache, scopeFactory.Object, logger.Object);
        return (sut, mockUow, cache);
    }

    [Fact]
    public async Task GetAsync_OnFirstCall_QueriesDatabaseAndReturnsVm()
    {
        var (sut, mockUow, _) = CreateSut();

        var vm = await sut.GetAsync(CancellationToken.None);

        vm.Should().NotBeNull();
        vm.DatabaseAvailable.Should().BeTrue();
        mockUow.Object.Positions.Should().NotBeNull();
    }

    [Fact]
    public async Task GetAsync_OnSecondCallWithin60s_ReturnsCachedResult()
    {
        var (sut, mockUow, _) = CreateSut();

        // First call populates cache.
        await sut.GetAsync(CancellationToken.None);

        // Second call should be served from cache without issuing additional DB calls.
        var secondVm = await sut.GetAsync(CancellationToken.None);

        secondVm.Should().NotBeNull();
        // Verify bot config was queried only once across both GetAsync calls.
        Mock.Get(mockUow.Object.BotConfig).Verify(r => r.GetActiveAsync(), Times.Once);
    }

    [Fact]
    public async Task GetAsync_WhenReconciliationJsonMalformed_DegradesSectionOnly()
    {
        var (sut, _, _) = CreateSut(uow =>
        {
            var mockRecon = new Mock<IReconciliationReportRepository>();
            mockRecon.Setup(r => r.GetMostRecentAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ReconciliationReport
                {
                    RunAtUtc = DateTime.UtcNow,
                    OverallStatus = "Healthy",
                    PerExchangeEquityJson = "{not-json",
                    DegradedExchangesJson = string.Empty,
                });
            uow.Setup(u => u.ReconciliationReports).Returns(mockRecon.Object);
        });

        var vm = await sut.GetAsync(CancellationToken.None);

        vm.Reconciliation.Should().NotBeNull();
        vm.Reconciliation!.PerExchangeEquityMalformed.Should().BeTrue();
        vm.Reconciliation.PerExchangeEquity.Should().BeNull();
        vm.Reconciliation.Report.OverallStatus.Should().Be("Healthy");
        // Other sections still accessible.
        vm.DatabaseAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task GetAsync_WhenSignalEngineDiagnosticsNotAvailable_RendersPlaceholderSection()
    {
        var (sut, _, _) = CreateSut();

        var vm = await sut.GetAsync(CancellationToken.None);

        vm.SkipReasons.Available.Should().BeFalse();
    }

    [Fact]
    public async Task GetAsync_WhenDatabaseUnavailableException_ReturnsDegradedVm()
    {
        var (sut, _, cache) = CreateSut(uow =>
        {
            var mockBotConfig = new Mock<IBotConfigRepository>();
            mockBotConfig.Setup(r => r.GetActiveAsync())
                .ThrowsAsync(new DatabaseUnavailableException("DB down"));
            uow.Setup(u => u.BotConfig).Returns(mockBotConfig.Object);
        });

        var vm = await sut.GetAsync(CancellationToken.None);

        vm.DatabaseAvailable.Should().BeFalse();
        vm.DegradedReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetAsync_PnlAttributionRowsMatchSqlIdentity()
    {
        var pnlRows = new List<PnlAttributionWindowDto>
        {
            new() { Window = "7d", GrossFunding = 100m, EntryFees = 5m, ExitFees = 5m, NetRealized = 85m, SlippageResidual = 5m },
            new() { Window = "30d", GrossFunding = 200m, EntryFees = 10m, ExitFees = 10m, NetRealized = 170m, SlippageResidual = 10m },
            new() { Window = "Lifetime", GrossFunding = 600m, EntryFees = 30m, ExitFees = 30m, NetRealized = 510m, SlippageResidual = 30m },
        };

        var (sut, _, _) = CreateSut(uow =>
        {
            var mockPositions = new Mock<IPositionRepository>();
            mockPositions.Setup(r => r.GetPnlAttributionWindowsAsync(It.IsAny<IReadOnlyList<DateTime>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(pnlRows);
            mockPositions.Setup(r => r.GetHoldTimeBucketsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<HoldTimeBucketDto>());
            mockPositions.Setup(r => r.CountEmergencyClosedZeroFillSinceAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
            mockPositions.Setup(r => r.CountPhantomFeeRowsSinceAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
            mockPositions.Setup(r => r.GetPerExchangePairKpiAsync(It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<ExchangePairKpiAggregateDto>());
            mockPositions.Setup(r => r.GetPerAssetKpiAsync(It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<AssetKpiAggregateDto>());
            mockPositions.Setup(r => r.GetRecentFailedOpensAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<FailedOpenEventDto>());
            uow.Setup(u => u.Positions).Returns(mockPositions.Object);
        });

        var vm = await sut.GetAsync(CancellationToken.None);

        vm.PnlAttribution.Should().HaveCount(3);
        foreach (var row in vm.PnlAttribution)
        {
            // SQL identity: SlippageResidual = GrossFunding - EntryFees - ExitFees - NetRealized
            var expected = row.GrossFunding - row.EntryFees - row.ExitFees - row.NetRealized;
            row.SlippageResidual.Should().Be(expected,
                $"slippage residual identity must hold for window '{row.Window}'");
        }
    }
}
