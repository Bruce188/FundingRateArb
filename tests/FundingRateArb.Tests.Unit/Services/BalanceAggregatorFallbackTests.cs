using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class BalanceAggregatorFallbackTests
{
    private readonly Mock<IUserSettingsService> _mockUserSettings = new();
    private readonly Mock<IExchangeConnectorFactory> _mockConnectorFactory = new();
    private readonly Mock<ICircuitBreakerManager> _mockCircuitBreaker = new();
    private readonly Mock<ILogger<BalanceAggregator>> _mockLogger = new();

    private BalanceAggregator BuildSut(out MemoryCache cache)
    {
        cache = new MemoryCache(new MemoryCacheOptions());
        return new BalanceAggregator(
            _mockUserSettings.Object,
            _mockConnectorFactory.Object,
            cache,
            _mockCircuitBreaker.Object,
            _mockLogger.Object);
    }

    [Fact]
    public async Task Fetch_OnSuccess_RecordsLastKnown()
    {
        const string userId = "fb-user-success";
        const string exchange = "TestExchange";
        var sut = BuildSut(out var cache);

        var creds = new List<UserExchangeCredential>
        {
            new() { Id = 1, ExchangeId = 1, Exchange = new Exchange { Id = 1, Name = exchange }, EncryptedApiKey = "k" },
        };
        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync(userId)).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(("key", "secret", (string?)null, (string?)null, (string?)null, (string?)null));

        var connector = new Mock<IExchangeConnector>();
        connector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(250m);
        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync(exchange, "key", "secret", null, null, null, null, It.IsAny<string?>()))
            .ReturnsAsync(connector.Object);

        await sut.GetBalanceSnapshotAsync(userId);

        var lkgKey = $"balance:lastgood:{userId}:{exchange}";
        cache.TryGetValue<(decimal Value, DateTime FetchedAt)>(lkgKey, out var entry).Should().BeTrue();
        entry.Value.Should().Be(250m);
        entry.FetchedAt.Should().BeCloseTo(DateTime.UtcNow, precision: TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Fetch_OnFailureWithin5Min_ReturnsStaleWithLastKnown()
    {
        const string userId = "fb-user-within5";
        const string exchange = "TestExchange";
        var sut = BuildSut(out var cache);

        var creds = new List<UserExchangeCredential>
        {
            new() { Id = 1, ExchangeId = 1, Exchange = new Exchange { Id = 1, Name = exchange }, EncryptedApiKey = "k" },
        };
        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync(userId)).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(("key", "secret", (string?)null, (string?)null, (string?)null, (string?)null));

        var connector = new Mock<IExchangeConnector>();
        connector
            .SetupSequence(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(100m)
            .ThrowsAsync(new HttpRequestException("timeout"));
        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync(exchange, "key", "secret", null, null, null, null, It.IsAny<string?>()))
            .ReturnsAsync(connector.Object);

        // First call populates LKG
        await sut.GetBalanceSnapshotAsync(userId);
        cache.Remove($"balance:{userId}");

        // Second call fails — LKG is within 5-minute window
        var result = await sut.GetBalanceSnapshotAsync(userId);

        result.Balances.Should().HaveCount(1);
        var balance = result.Balances[0];
        balance.IsStale.Should().BeTrue();
        balance.IsUnavailable.Should().BeFalse();
        balance.LastKnownAvailableUsdc.Should().Be(100m);
        balance.LastKnownAt.Should().NotBeNull();
        balance.AvailableUsdc.Should().Be(100m);
    }

    [Fact]
    public async Task Fetch_OnFailureAfter5Min_ReturnsUnavailable()
    {
        const string userId = "fb-user-after5";
        const string exchange = "TestExchange";
        var sut = BuildSut(out var cache);

        var creds = new List<UserExchangeCredential>
        {
            new() { Id = 1, ExchangeId = 1, Exchange = new Exchange { Id = 1, Name = exchange }, EncryptedApiKey = "k" },
        };
        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync(userId)).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(("key", "secret", (string?)null, (string?)null, (string?)null, (string?)null));

        var connector = new Mock<IExchangeConnector>();
        connector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("timeout"));
        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync(exchange, "key", "secret", null, null, null, null, It.IsAny<string?>()))
            .ReturnsAsync(connector.Object);

        // Pre-populate LKG with a timestamp older than the 5-minute fallback window
        var oldTimestamp = DateTime.UtcNow.AddMinutes(-10);
        cache.Set($"balance:lastgood:{userId}:{exchange}", (Value: 99m, FetchedAt: oldTimestamp),
            new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(24) });

        var result = await sut.GetBalanceSnapshotAsync(userId);

        result.Balances.Should().HaveCount(1);
        var balance = result.Balances[0];
        balance.IsUnavailable.Should().BeTrue();
        balance.IsStale.Should().BeFalse();
        balance.LastKnownAvailableUsdc.Should().BeNull();
        balance.LastKnownAt.Should().BeNull();
        balance.AvailableUsdc.Should().Be(0m);
    }

    [Fact]
    public async Task Fetch_OnSuccessAfterFailure_ClearsStaleAndUnavailable()
    {
        const string userId = "fb-user-recovery";
        const string exchange = "TestExchange";
        var sut = BuildSut(out var cache);

        var creds = new List<UserExchangeCredential>
        {
            new() { Id = 1, ExchangeId = 1, Exchange = new Exchange { Id = 1, Name = exchange }, EncryptedApiKey = "k" },
        };
        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync(userId)).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(("key", "secret", (string?)null, (string?)null, (string?)null, (string?)null));

        var connector = new Mock<IExchangeConnector>();
        connector
            .SetupSequence(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(100m)           // 1st call: success
            .ThrowsAsync(new HttpRequestException("timeout")) // 2nd call: failure
            .ReturnsAsync(150m);          // 3rd call: recovery
        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync(exchange, "key", "secret", null, null, null, null, It.IsAny<string?>()))
            .ReturnsAsync(connector.Object);

        // 1st call: success, populates LKG
        await sut.GetBalanceSnapshotAsync(userId);
        cache.Remove($"balance:{userId}");

        // 2nd call: failure, stale fallback used
        var staleResult = await sut.GetBalanceSnapshotAsync(userId);
        staleResult.Balances[0].IsStale.Should().BeTrue();
        cache.Remove($"balance:{userId}");

        // 3rd call: success, exchange recovered
        var recoveredResult = await sut.GetBalanceSnapshotAsync(userId);

        recoveredResult.Balances.Should().HaveCount(1);
        var balance = recoveredResult.Balances[0];
        balance.IsStale.Should().BeFalse();
        balance.IsUnavailable.Should().BeFalse();
        balance.AvailableUsdc.Should().Be(150m);
        balance.LastKnownAvailableUsdc.Should().BeNull();
        balance.LastKnownAt.Should().BeNull();
    }

    [Fact]
    public async Task Fetch_PerExchangeIsolation_OneFailureDoesNotAffectOthers()
    {
        const string userId = "fb-user-isolation";
        const string exchangeA = "ExchangeA";
        const string exchangeB = "ExchangeB";
        var sut = BuildSut(out var cache);

        var creds = new List<UserExchangeCredential>
        {
            new() { Id = 1, ExchangeId = 1, Exchange = new Exchange { Id = 1, Name = exchangeA }, EncryptedApiKey = "k" },
            new() { Id = 2, ExchangeId = 2, Exchange = new Exchange { Id = 2, Name = exchangeB }, EncryptedApiKey = "k" },
        };
        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync(userId)).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(("key", "secret", (string?)null, (string?)null, (string?)null, (string?)null));

        var connectorA = new Mock<IExchangeConnector>();
        connectorA
            .SetupSequence(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(200m)
            .ThrowsAsync(new HttpRequestException("timeout"));

        var connectorB = new Mock<IExchangeConnector>();
        connectorB
            .SetupSequence(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(300m)
            .ReturnsAsync(300m);

        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync(exchangeA, "key", "secret", null, null, null, null, It.IsAny<string?>()))
            .ReturnsAsync(connectorA.Object);
        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync(exchangeB, "key", "secret", null, null, null, null, It.IsAny<string?>()))
            .ReturnsAsync(connectorB.Object);

        // 1st call: both succeed, both LKG entries written
        await sut.GetBalanceSnapshotAsync(userId);
        cache.Remove($"balance:{userId}");

        // 2nd call: A fails, B succeeds
        var result = await sut.GetBalanceSnapshotAsync(userId);

        result.Balances.Should().HaveCount(2);

        var aBalance = result.Balances.First(b => b.ExchangeName == exchangeA);
        aBalance.IsStale.Should().BeTrue("A failed but has recent LKG within 5-min window");
        aBalance.IsUnavailable.Should().BeFalse();
        aBalance.LastKnownAvailableUsdc.Should().Be(200m);

        var bBalance = result.Balances.First(b => b.ExchangeName == exchangeB);
        bBalance.IsStale.Should().BeFalse("B succeeded — must not be affected by A's failure");
        bBalance.IsUnavailable.Should().BeFalse();
        bBalance.AvailableUsdc.Should().Be(300m);
    }
}
