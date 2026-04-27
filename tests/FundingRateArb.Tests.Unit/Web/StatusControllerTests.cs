using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FundingRateArb.Application.Common;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Services;
using FundingRateArb.Application.ViewModels;
using FundingRateArb.Tests.Unit.Helpers;
using FundingRateArb.Web.Areas.Admin.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace FundingRateArb.Tests.Unit.Web;

public class StatusControllerTests
{
    private static StatusController CreateSut(Mock<IStatusPageAggregator> aggregator)
    {
        var logger = new Mock<ILogger<StatusController>>();
        var controller = new StatusController(aggregator.Object, logger.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    [Fact]
    public async Task Index_WhenDatabaseHealthy_ReturnsViewWithPopulatedVm()
    {
        // Arrange
        var aggregator = new Mock<IStatusPageAggregator>();
        var vm = new StatusViewModel
        {
            DatabaseAvailable = true,
            PnlAttribution = new List<PnlAttributionWindowDto>
            {
                new() { Window = "7d", GrossFunding = 100m, EntryFees = 5m, ExitFees = 5m, NetRealized = 80m, SlippageResidual = 10m },
                new() { Window = "30d", GrossFunding = 400m, EntryFees = 20m, ExitFees = 20m, NetRealized = 320m, SlippageResidual = 40m },
                new() { Window = "Lifetime", GrossFunding = 1200m, EntryFees = 60m, ExitFees = 60m, NetRealized = 960m, SlippageResidual = 120m },
            },
        };
        aggregator.Setup(a => a.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(vm);

        // Act
        var result = await CreateSut(aggregator).Index(CancellationToken.None);

        // Assert
        var view = result.Should().BeOfType<ViewResult>().Subject;
        var model = view.Model.Should().BeOfType<StatusViewModel>().Subject;
        model.DatabaseAvailable.Should().BeTrue();
        model.PnlAttribution.Should().HaveCount(3);
    }

    [Fact]
    public async Task Index_WhenDatabaseUnavailableException_ReturnsDegradedView()
    {
        var aggregator = new Mock<IStatusPageAggregator>();
        aggregator.Setup(a => a.GetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DatabaseUnavailableException("test"));

        var result = await CreateSut(aggregator).Index(CancellationToken.None);

        var view = result.Should().BeOfType<ViewResult>().Subject;
        var model = view.Model.Should().BeOfType<StatusViewModel>().Subject;
        model.DatabaseAvailable.Should().BeFalse();
        model.DegradedReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Index_WhenTransientSqlException_ReturnsDegradedView()
    {
        var aggregator = new Mock<IStatusPageAggregator>();
        // Use a known transient code from SqlTransientErrorNumbers (e.g., 10928).
        aggregator.Setup(a => a.GetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(SqlExceptionFactory.Create(10928));

        var result = await CreateSut(aggregator).Index(CancellationToken.None);

        var view = result.Should().BeOfType<ViewResult>().Subject;
        var model = view.Model.Should().BeOfType<StatusViewModel>().Subject;
        model.DatabaseAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task Index_WhenPhantomFeeAndFeature5MetricBothPresent_DistinctCountersInVm()
    {
        var aggregator = new Mock<IStatusPageAggregator>();
        var vm = new StatusViewModel
        {
            DatabaseAvailable = true,
            PhantomFee = new PhantomFeeIndicator
            {
                EmergencyClosedZeroFill24h = 5,
                EmergencyClosedZeroFill7d = 12,
                FailedNullOrderId24h = 2,
            },
        };
        aggregator.Setup(a => a.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(vm);

        var result = await CreateSut(aggregator).Index(CancellationToken.None);

        var model = (result as ViewResult)!.Model as StatusViewModel;
        model!.PhantomFee.EmergencyClosedZeroFill24h.Should().Be(5);
        model.PhantomFee.FailedNullOrderId24h.Should().Be(2);
        // Verify the two metrics are NOT collapsed.
        model.PhantomFee.EmergencyClosedZeroFill24h.Should().NotBe(model.PhantomFee.FailedNullOrderId24h);
    }
}
