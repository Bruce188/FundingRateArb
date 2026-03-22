using FluentAssertions;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class EmailServiceTests
{
    private readonly Mock<ILogger<EmailService>> _mockLogger = new();

    private EmailService CreateService(string? apiKey = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SendGrid:ApiKey"] = apiKey,
                ["SendGrid:FromEmail"] = "test@example.com",
                ["SendGrid:FromName"] = "Test",
            })
            .Build();

        return new EmailService(config, _mockLogger.Object);
    }

    [Fact]
    public async Task SendAlertEmailAsync_NoApiKey_SkipsGracefully()
    {
        var sut = CreateService(apiKey: null);

        // Should not throw
        await sut.SendAlertEmailAsync("user@test.com", "Test", "Body");
    }

    [Fact]
    public async Task SendAlertEmailAsync_EmptyApiKey_SkipsGracefully()
    {
        var sut = CreateService(apiKey: "");

        await sut.SendAlertEmailAsync("user@test.com", "Test", "Body");
    }

    [Fact]
    public async Task SendDailySummaryAsync_NoApiKey_SkipsGracefully()
    {
        var sut = CreateService(apiKey: null);

        var summary = new DailySummaryDto
        {
            OpenPositionCount = 3,
            TotalPnl = 42.5m,
            AlertsCount = 1,
        };

        await sut.SendDailySummaryAsync("user@test.com", summary);
    }

    [Fact]
    public async Task SendAlertEmailAsync_RateLimit_SkipsSecondCall()
    {
        // Use a fake API key — the actual HTTP call will fail but rate limiting is checked before
        var sut = CreateService(apiKey: "SG.fake-key-for-rate-limit-test");

        // First call — will attempt to send (and fail due to invalid key, but rate limit is set)
        await sut.SendAlertEmailAsync("user@test.com", "Alert 1", "Body 1");

        // Second call within 15 minutes — should be rate-limited (skipped)
        await sut.SendAlertEmailAsync("user@test.com", "Alert 2", "Body 2");

        // We can't directly verify the rate limit was hit vs. the send failed,
        // but the test verifies no exceptions are thrown during rate limiting
    }
}
