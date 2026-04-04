using System.Security.Claims;
using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Web.Controllers;
using FundingRateArb.Web.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Controllers;

public class SettingsControllerTests
{
    private readonly Mock<IUserSettingsService> _mockSettings = new();
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IBotConfigRepository> _mockBotConfigRepo = new();
    private readonly SettingsController _controller;

    private static readonly BotConfiguration GlobalConfig = new()
    {
        IsEnabled = true,
        TotalCapitalUsdc = 5000m,
        DefaultLeverage = 5,
        MaxLeverageCap = 50,
        MaxConcurrentPositions = 3,
        MaxCapitalPerPosition = 0.5m,
        OpenThreshold = 0.0003m,
        CloseThreshold = 0.0001m,
        AlertThreshold = 0.00015m,
        StopLossPct = 0.15m,
        MaxHoldTimeHours = 48,
        DailyDrawdownPausePct = 0.05m,
        ConsecutiveLossPause = 3,
        MaxExposurePerAsset = 0.5m,
        MaxExposurePerExchange = 0.7m,
        AllocationStrategy = AllocationStrategy.Concentrated,
        AllocationTopN = 5,
        FeeAmortizationHours = 24,
        MinPositionSizeUsdc = 10m,
        MinVolume24hUsdc = 100000m,
        RateStalenessMinutes = 10,
        FundingWindowMinutes = 30,
        UpdatedByUserId = "admin-user-id",
    };

    public SettingsControllerTests()
    {
        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfigRepo.Object);
        _mockBotConfigRepo.Setup(b => b.GetActiveAsync()).ReturnsAsync(GlobalConfig);

        _controller = new SettingsController(
            _mockSettings.Object,
            _mockUow.Object,
            NullLogger<SettingsController>.Instance);
    }

    private void SetupAuthenticatedUser()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "test-user-id"),
        }, "mock"));
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };
        _controller.TempData = new TempDataDictionary(
            _controller.ControllerContext.HttpContext,
            Mock.Of<ITempDataProvider>());
    }

    [Fact]
    public async Task Configuration_Get_ReturnsViewWithAdminDefaults()
    {
        SetupAuthenticatedUser();
        _mockSettings
            .Setup(s => s.GetOrCreateConfigAsync("test-user-id"))
            .ReturnsAsync(new UserConfiguration
            {
                IsEnabled = true,
                TotalCapitalUsdc = 1000m,
                DefaultLeverage = 3,
                MaxConcurrentPositions = 2,
                MaxCapitalPerPosition = 0.5m,
                OpenThreshold = 0.0003m,
                CloseThreshold = 0.0001m,
                AlertThreshold = 0.00015m,
                StopLossPct = 0.15m,
                MaxHoldTimeHours = 48,
                AllocationStrategy = AllocationStrategy.Concentrated,
                AllocationTopN = 5,
                FeeAmortizationHours = 24m,
                MinPositionSizeUsdc = 10m,
                MinVolume24hUsdc = 100000m,
                RateStalenessMinutes = 10,
                DailyDrawdownPausePct = 0.05m,
                ConsecutiveLossPause = 3,
                FundingWindowMinutes = 30,
                MaxExposurePerAsset = 0.5m,
                MaxExposurePerExchange = 0.7m,
            });

        var result = await _controller.Configuration();

        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<UserConfigViewModel>().Subject;

        // AdminDefaults must be populated from the global BotConfiguration
        model.AdminDefaults.Should().NotBeNull();
        model.AdminDefaults!.TotalCapitalUsdc.Should().Be(GlobalConfig.TotalCapitalUsdc);
        model.AdminDefaults.DefaultLeverage.Should().Be(GlobalConfig.DefaultLeverage);
        model.AdminDefaults.MaxConcurrentPositions.Should().Be(GlobalConfig.MaxConcurrentPositions);
        model.AdminDefaults.OpenThreshold.Should().Be(GlobalConfig.OpenThreshold);
        model.AdminDefaults.StopLossPct.Should().Be(GlobalConfig.StopLossPct);
    }

    [Fact]
    public async Task Configuration_Get_Unauthenticated_ReturnsUnauthorized()
    {
        // No user claims set
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        var result = await _controller.Configuration();

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task Configuration_Get_NullGlobalConfig_ReturnsViewWithEmptyDefaults()
    {
        SetupAuthenticatedUser();
        _mockBotConfigRepo.Setup(b => b.GetActiveAsync()).Returns(Task.FromResult<BotConfiguration>(null!));
        _mockSettings
            .Setup(s => s.GetOrCreateConfigAsync("test-user-id"))
            .ReturnsAsync(new UserConfiguration
            {
                IsEnabled = true,
                TotalCapitalUsdc = 1000m,
                DefaultLeverage = 3,
                MaxConcurrentPositions = 2,
            });

        // Should not throw NullReferenceException
        var result = await _controller.Configuration();

        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<UserConfigViewModel>().Subject;

        // AdminDefaults should be an empty DTO with default values, not null
        model.AdminDefaults.Should().NotBeNull();
        model.AdminDefaults!.TotalCapitalUsdc.Should().Be(0m);
        model.AdminDefaults.DefaultLeverage.Should().Be(0);
    }

    [Fact]
    public async Task ResetConfiguration_NullGlobalConfig_RedirectsWithErrorMessage()
    {
        SetupAuthenticatedUser();
        _mockBotConfigRepo.Setup(b => b.GetActiveAsync()).Returns(Task.FromResult<BotConfiguration>(null!));

        var result = await _controller.ResetConfiguration();

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Configuration");
        _controller.TempData["Error"].Should().Be("No global configuration found. Cannot reset to defaults.");
    }

    [Fact]
    public async Task Configuration_Post_WithDefaultValues_Succeeds()
    {
        // Arrange — simulate a new user POSTing with entity default values
        SetupAuthenticatedUser();
        var config = new UserConfiguration { UserId = "test-user-id" };
        _mockSettings.Setup(s => s.GetOrCreateConfigAsync("test-user-id")).ReturnsAsync(config);

        // Build a ViewModel with all the entity defaults
        var model = new UserConfigViewModel
        {
            IsEnabled = false,
            OpenThreshold = 0.0002m,
            CloseThreshold = 0.00005m,
            AlertThreshold = 0.00015m,
            DefaultLeverage = 5,
            TotalCapitalUsdc = 39m,
            MaxCapitalPerPosition = 0.90m,
            MaxConcurrentPositions = 1,
            StopLossPct = 0.10m,
            MaxHoldTimeHours = 48,
            AllocationStrategy = AllocationStrategy.Concentrated,
            AllocationTopN = 3,
            FeeAmortizationHours = 12m,
            MinPositionSizeUsdc = 5m,
            MinVolume24hUsdc = 50000m,
            RateStalenessMinutes = 15,
            DailyDrawdownPausePct = 0.08m,
            ConsecutiveLossPause = 3,
            FundingWindowMinutes = 10,
            MaxExposurePerAsset = 0.5m,
            MaxExposurePerExchange = 0.7m,
        };

        // Act
        var result = await _controller.Configuration(model);

        // Assert — should redirect to Configuration (success), not return View (validation failure)
        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Configuration");
        _mockSettings.Verify(s => s.UpdateConfigAsync("test-user-id", It.IsAny<UserConfiguration>()), Times.Once);
    }

    [Fact]
    public async Task Configuration_Post_WithOnlyIsEnabledChanged_Succeeds()
    {
        // Arrange — simulate toggling the "Enable my bot" switch with all other defaults
        SetupAuthenticatedUser();
        var config = new UserConfiguration { UserId = "test-user-id" };
        _mockSettings.Setup(s => s.GetOrCreateConfigAsync("test-user-id")).ReturnsAsync(config);

        var model = new UserConfigViewModel
        {
            IsEnabled = true, // changed from default false
            OpenThreshold = 0.0002m,
            CloseThreshold = 0.00005m,
            AlertThreshold = 0.00015m,
            DefaultLeverage = 5,
            TotalCapitalUsdc = 39m,
            MaxCapitalPerPosition = 0.90m,
            MaxConcurrentPositions = 1,
            StopLossPct = 0.10m,
            MaxHoldTimeHours = 48,
            AllocationStrategy = AllocationStrategy.Concentrated,
            AllocationTopN = 3,
            FeeAmortizationHours = 12m,
            MinPositionSizeUsdc = 5m,
            MinVolume24hUsdc = 50000m,
            RateStalenessMinutes = 15,
            DailyDrawdownPausePct = 0.08m,
            ConsecutiveLossPause = 3,
            FundingWindowMinutes = 10,
            MaxExposurePerAsset = 0.5m,
            MaxExposurePerExchange = 0.7m,
        };

        // Act
        var result = await _controller.Configuration(model);

        // Assert — should succeed
        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Configuration");
        _mockSettings.Verify(s => s.UpdateConfigAsync("test-user-id", It.Is<UserConfiguration>(c => c.IsEnabled == true)), Times.Once);
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("0x1234567890abcdef1234567890abcdef1234567")]   // 39 hex chars — too short
    [InlineData("0x1234567890abcdef1234567890abcdef123456789")] // 41 hex chars — too long
    [InlineData("0xGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")] // non-hex chars, correct length
    public async Task SaveApiKey_WithInvalidEthereumSubAccountAddress_ReturnsError(string subAccountAddress)
    {
        SetupAuthenticatedUser();

        var result = await _controller.SaveApiKey(
            exchangeId: 1,
            apiKey: null,
            apiSecret: null,
            walletAddress: "0xabc",
            privateKey: "some-key",
            subAccountAddress: subAccountAddress,
            apiKeyIndex: null);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("ApiKeys");
        _controller.TempData["Error"].Should().NotBeNull();
        _controller.TempData["Error"]!.ToString().Should().Contain("Ethereum address");
        _mockSettings.Verify(s => s.SaveCredentialAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Theory]
    [InlineData("0x1234567890abcdef1234567890abcdef12345678")]   // lowercase hex
    [InlineData("0x1234567890ABCDEF1234567890ABCDEF12345678")]   // uppercase hex
    public async Task SaveApiKey_WithValidSubAccountAddress_Succeeds(string subAccountAddress)
    {
        SetupAuthenticatedUser();

        var result = await _controller.SaveApiKey(
            exchangeId: 1,
            apiKey: null,
            apiSecret: null,
            walletAddress: "0xabc123",
            privateKey: "some-key",
            subAccountAddress: subAccountAddress,
            apiKeyIndex: null);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("ApiKeys");
        _controller.TempData["Error"].Should().BeNull();
        _mockSettings.Verify(s => s.SaveCredentialAsync(
            "test-user-id", 1, null, null, "0xabc123", "some-key",
            subAccountAddress, null), Times.Once);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("255")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("99999999999")]
    public async Task SaveApiKey_WithOutOfRangeApiKeyIndex_ReturnsError(string apiKeyIndex)
    {
        SetupAuthenticatedUser();

        var result = await _controller.SaveApiKey(
            exchangeId: 2,
            apiKey: null,
            apiSecret: null,
            walletAddress: null,
            privateKey: "some-key",
            subAccountAddress: null,
            apiKeyIndex: apiKeyIndex);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("ApiKeys");
        _controller.TempData["Error"].Should().NotBeNull();
        _controller.TempData["Error"]!.ToString().Should().Contain("2 and 254");
        _mockSettings.Verify(s => s.SaveCredentialAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("254")]
    public async Task SaveApiKey_WithValidApiKeyIndex_Succeeds(string apiKeyIndex)
    {
        SetupAuthenticatedUser();

        var result = await _controller.SaveApiKey(
            exchangeId: 2,
            apiKey: null,
            apiSecret: null,
            walletAddress: null,
            privateKey: "some-key",
            subAccountAddress: null,
            apiKeyIndex: apiKeyIndex);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("ApiKeys");
        _controller.TempData["Error"].Should().BeNull();
        _mockSettings.Verify(s => s.SaveCredentialAsync(
            "test-user-id", 2, null, null, null, "some-key",
            null, apiKeyIndex), Times.Once);
    }

    [Fact]
    public async Task Preferences_FiltersOutDataOnlyExchanges()
    {
        // Arrange
        SetupAuthenticatedUser();
        var exchanges = new List<Exchange>
        {
            new() { Id = 1, Name = "Hyperliquid", IsActive = true, IsDataOnly = false },
            new() { Id = 2, Name = "Lighter", IsActive = true, IsDataOnly = false },
            new() { Id = 4, Name = "CoinGlass", IsActive = true, IsDataOnly = true },
        };
        _mockSettings.Setup(s => s.GetAvailableExchangesAsync()).ReturnsAsync(exchanges);
        _mockSettings.Setup(s => s.GetUserEnabledExchangeIdsAsync("test-user-id")).ReturnsAsync(new List<int> { 1, 2 });
        _mockSettings.Setup(s => s.GetActiveCredentialsAsync("test-user-id")).ReturnsAsync(new List<UserExchangeCredential>());
        _mockSettings.Setup(s => s.GetAvailableAssetsAsync()).ReturnsAsync(new List<Asset>());
        _mockSettings.Setup(s => s.GetUserEnabledAssetIdsAsync("test-user-id")).ReturnsAsync(new List<int>());

        // Act
        var result = await _controller.Preferences();

        // Assert — CoinGlass (data-only) should NOT appear in the ViewModel
        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<PreferencesViewModel>().Subject;
        model.Exchanges.Should().HaveCount(2);
        model.Exchanges.Should().NotContain(e => e.ExchangeName == "CoinGlass");
    }
}
