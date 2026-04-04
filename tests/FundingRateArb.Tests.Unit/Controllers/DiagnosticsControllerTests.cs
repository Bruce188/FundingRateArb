using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Moq;

namespace FundingRateArb.Tests.Unit.Controllers;

public class DiagnosticsControllerTests
{
    private const string ValidApiKey = "test-diag-key-12345";

    private readonly Mock<IUnitOfWork> _mockUow;
    private readonly Mock<IBotDiagnostics> _mockDiagnostics;
    private readonly Mock<IBotControl> _mockBotControl;
    private readonly Mock<IExecutionEngine> _mockExecutionEngine;
    private readonly Mock<IConfiguration> _mockConfig;
    private readonly Mock<IPositionRepository> _mockPositions;
    private readonly Mock<IAlertRepository> _mockAlerts;
    private readonly Mock<IFundingRateRepository> _mockFundingRates;
    private readonly Mock<IBotConfigRepository> _mockBotConfig;
    private readonly DiagnosticsController _controller;

    public DiagnosticsControllerTests()
    {
        _mockUow = new Mock<IUnitOfWork>();
        _mockDiagnostics = new Mock<IBotDiagnostics>();
        _mockBotControl = new Mock<IBotControl>();
        _mockExecutionEngine = new Mock<IExecutionEngine>();
        _mockConfig = new Mock<IConfiguration>();
        _mockPositions = new Mock<IPositionRepository>();
        _mockAlerts = new Mock<IAlertRepository>();
        _mockFundingRates = new Mock<IFundingRateRepository>();
        _mockBotConfig = new Mock<IBotConfigRepository>();

        _mockUow.Setup(u => u.Positions).Returns(_mockPositions.Object);
        _mockUow.Setup(u => u.Alerts).Returns(_mockAlerts.Object);
        _mockUow.Setup(u => u.FundingRates).Returns(_mockFundingRates.Object);
        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfig.Object);
        _mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        _mockConfig.Setup(c => c["Diagnostics:ApiKey"]).Returns(ValidApiKey);

        _mockDiagnostics.Setup(d => d.GetCircuitBreakerStates())
            .Returns(new List<CircuitBreakerStatusDto>());
        _mockPositions.Setup(p => p.CountByStatusAsync(It.IsAny<PositionStatus>()))
            .ReturnsAsync(0);
        _mockAlerts.Setup(a => a.GetSeverityCountsAsync(It.IsAny<TimeSpan>()))
            .ReturnsAsync(new Dictionary<AlertSeverity, int>());
        _mockAlerts.Setup(a => a.GetRecentUnreadAsync(It.IsAny<TimeSpan>()))
            .ReturnsAsync(new List<Alert>());
        _mockPositions.Setup(p => p.GetClosedSinceAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<ArbitragePosition>());
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>());

        _controller = new DiagnosticsController(
            _mockUow.Object,
            _mockDiagnostics.Object,
            _mockBotControl.Object,
            _mockExecutionEngine.Object,
            _mockConfig.Object,
            Mock.Of<ILogger<DiagnosticsController>>());
    }

    private void SetApiKeyHeader(string? key)
    {
        var httpContext = new DefaultHttpContext();
        if (key is not null)
        {
            httpContext.Request.Headers["X-Diagnostics-Key"] = key;
        }
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    // ── GetSummary tests ────────────────────────────────────────────────────

    [Fact]
    public async Task GetSummary_ValidKey_ReturnsOkWithSummary()
    {
        SetApiKeyHeader(ValidApiKey);

        var result = await _controller.GetSummary(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetSummary_MissingKey_Returns401()
    {
        SetApiKeyHeader(null);

        var result = await _controller.GetSummary(CancellationToken.None);

        var unauthorized = result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        unauthorized.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task GetSummary_InvalidKey_Returns401()
    {
        SetApiKeyHeader("wrong-key");

        var result = await _controller.GetSummary(CancellationToken.None);

        var unauthorized = result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        unauthorized.StatusCode.Should().Be(401);
    }

    // ── ExecuteAction tests ─────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAction_ClearCircuitBreakers_ReturnsOk()
    {
        SetApiKeyHeader(ValidApiKey);

        var request = new DiagnosticsActionRequest { Action = "clear_circuit_breakers" };

        var result = await _controller.ExecuteAction(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        _mockBotControl.Verify(b => b.ClearCooldowns(), Times.Once);
    }

    [Fact]
    public async Task ExecuteAction_ToggleDryRun_ReturnsOk()
    {
        SetApiKeyHeader(ValidApiKey);

        var config = new BotConfiguration { DryRunEnabled = false };
        _mockBotConfig.Setup(r => r.GetActiveTrackedAsync()).ReturnsAsync(config);

        var request = new DiagnosticsActionRequest { Action = "toggle_dry_run" };

        var result = await _controller.ExecuteAction(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        config.DryRunEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAction_InvalidAction_ReturnsBadRequest()
    {
        SetApiKeyHeader(ValidApiKey);

        var request = new DiagnosticsActionRequest { Action = "invalid_action" };

        var result = await _controller.ExecuteAction(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ExecuteAction_MissingKey_Returns401()
    {
        SetApiKeyHeader(null);

        var request = new DiagnosticsActionRequest { Action = "clear_circuit_breakers" };

        var result = await _controller.ExecuteAction(request, CancellationToken.None);

        var unauthorized = result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        unauthorized.StatusCode.Should().Be(401);
    }
}
