using FluentAssertions;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FundingRateArb.Tests.Unit.Services;

public class CircuitBreakerManagerTests
{
    private readonly CircuitBreakerManager _sut;

    public CircuitBreakerManagerTests()
    {
        _sut = new CircuitBreakerManager(NullLogger<CircuitBreakerManager>.Instance);
    }

    // ── SweepExpiredEntries ───────────────────────────────────────────────────

    [Fact]
    public void SweepExpiredEntries_RemovesExpiredCircuitBreakers_PreservesActive()
    {
        _sut.ExchangeCircuitBreaker[1] = (3, DateTime.UtcNow.AddMinutes(-5)); // expired
        _sut.ExchangeCircuitBreaker[2] = (3, DateTime.UtcNow.AddMinutes(10)); // active
        _sut.ExchangeCircuitBreaker[3] = (1, DateTime.MinValue); // sub-threshold — not swept

        _sut.SweepExpiredEntries();

        _sut.ExchangeCircuitBreaker.Should().NotContainKey(1);
        _sut.ExchangeCircuitBreaker.Should().ContainKey(2);
        _sut.ExchangeCircuitBreaker.Should().ContainKey(3, "sub-threshold entries are not swept");
    }

    [Fact]
    public void SweepExpiredEntries_RemovesStaleCooldowns_PreservesRecent()
    {
        // Expired more than MaxCooldown ago — should be swept
        _sut.FailedOpCooldowns["stale"] = (DateTime.UtcNow.AddMinutes(-65), 2);
        // Expired recently (within MaxCooldown) — should be preserved for backoff continuity
        _sut.FailedOpCooldowns["recent"] = (DateTime.UtcNow.AddMinutes(-5), 1);
        // Still active — should be preserved
        _sut.FailedOpCooldowns["active"] = (DateTime.UtcNow.AddMinutes(5), 1);

        _sut.SweepExpiredEntries();

        _sut.FailedOpCooldowns.Should().NotContainKey("stale");
        _sut.FailedOpCooldowns.Should().ContainKey("recent");
        _sut.FailedOpCooldowns.Should().ContainKey("active");
    }

    [Fact]
    public void SweepExpiredEntries_RemovesExpiredAssetCooldowns()
    {
        _sut.AssetExchangeCooldowns[(1, 1)] = (3, DateTime.UtcNow.AddMinutes(-1)); // expired
        _sut.AssetExchangeCooldowns[(2, 1)] = (3, DateTime.UtcNow.AddMinutes(10)); // active
        _sut.AssetExchangeCooldowns[(3, 1)] = (1, DateTime.MinValue); // pre-threshold — not swept

        _sut.SweepExpiredEntries();

        _sut.AssetExchangeCooldowns.Should().NotContainKey((1, 1));
        _sut.AssetExchangeCooldowns.Should().ContainKey((2, 1));
        _sut.AssetExchangeCooldowns.Should().ContainKey((3, 1));
    }

    [Fact]
    public void SweepExpiredEntries_RemovesExpiredRotationCooldowns()
    {
        _sut.RotationCooldowns["expired"] = DateTime.UtcNow.AddMinutes(-1);
        _sut.RotationCooldowns["active"] = DateTime.UtcNow.AddMinutes(10);

        _sut.SweepExpiredEntries();

        _sut.RotationCooldowns.Should().NotContainKey("expired");
        _sut.RotationCooldowns.Should().ContainKey("active");
    }

    // ── IncrementExchangeFailure ──────────────────────────────────────────────

    [Fact]
    public void IncrementExchangeFailure_OpensCircuitBreaker_WhenThresholdReached()
    {
        var config = new BotConfiguration
        {
            ExchangeCircuitBreakerThreshold = 3,
            ExchangeCircuitBreakerMinutes = 15,
        };

        _sut.IncrementExchangeFailure(1, config);
        _sut.IncrementExchangeFailure(1, config);
        _sut.ExchangeCircuitBreaker[1].BrokenUntil.Should().Be(DateTime.MinValue,
            "CB should not open below threshold");

        _sut.IncrementExchangeFailure(1, config);
        _sut.ExchangeCircuitBreaker[1].Failures.Should().Be(3);
        _sut.ExchangeCircuitBreaker[1].BrokenUntil.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public void IncrementExchangeFailure_TracksSubThreshold()
    {
        var config = new BotConfiguration
        {
            ExchangeCircuitBreakerThreshold = 5,
            ExchangeCircuitBreakerMinutes = 10,
        };

        _sut.IncrementExchangeFailure(42, config);
        _sut.ExchangeCircuitBreaker.Should().ContainKey(42);
        _sut.ExchangeCircuitBreaker[42].Failures.Should().Be(1);
        _sut.ExchangeCircuitBreaker[42].BrokenUntil.Should().Be(DateTime.MinValue);
    }

    [Fact]
    public void IncrementExchangeFailure_SubThreshold_ReturnsFalse()
    {
        var config = new BotConfiguration
        {
            ExchangeCircuitBreakerThreshold = 3,
            ExchangeCircuitBreakerMinutes = 15,
        };

        _sut.IncrementExchangeFailure(1, config).Should().BeFalse();
        _sut.IncrementExchangeFailure(1, config).Should().BeFalse();
    }

    [Fact]
    public void IncrementExchangeFailure_AtThreshold_ReturnsTrue()
    {
        var config = new BotConfiguration
        {
            ExchangeCircuitBreakerThreshold = 3,
            ExchangeCircuitBreakerMinutes = 15,
        };

        _sut.IncrementExchangeFailure(1, config);
        _sut.IncrementExchangeFailure(1, config);
        _sut.IncrementExchangeFailure(1, config).Should().BeTrue(
            "third call reaches threshold and should signal transition");
    }

    [Fact]
    public void IncrementExchangeFailure_AlreadyOpen_ReturnsFalse()
    {
        var config = new BotConfiguration
        {
            ExchangeCircuitBreakerThreshold = 3,
            ExchangeCircuitBreakerMinutes = 15,
        };

        _sut.IncrementExchangeFailure(1, config);
        _sut.IncrementExchangeFailure(1, config);
        _sut.IncrementExchangeFailure(1, config); // opens

        _sut.IncrementExchangeFailure(1, config).Should().BeFalse(
            "circuit already open — no new transition");
    }

    [Fact]
    public void IncrementExchangeFailure_AuthError_DoesNotIncrement()
    {
        var config = new BotConfiguration
        {
            ExchangeCircuitBreakerThreshold = 1,
            ExchangeCircuitBreakerMinutes = 15,
        };

        var result = _sut.IncrementExchangeFailure(1, config, isAuthError: true);

        result.Should().BeFalse("auth errors should not trigger circuit breaker transition");
        _sut.ExchangeCircuitBreaker.Should().NotContainKey(1,
            "auth errors should not increment the failure counter");
    }

    [Fact]
    public void IncrementExchangeFailure_AuthError_DoesNotTripCircuitBreaker()
    {
        var config = new BotConfiguration
        {
            ExchangeCircuitBreakerThreshold = 3,
            ExchangeCircuitBreakerMinutes = 15,
        };

        // Two transient failures — just below threshold
        _sut.IncrementExchangeFailure(1, config);
        _sut.IncrementExchangeFailure(1, config);
        _sut.ExchangeCircuitBreaker[1].Failures.Should().Be(2);

        // Auth error should NOT push it over the threshold
        _sut.IncrementExchangeFailure(1, config, isAuthError: true);
        _sut.ExchangeCircuitBreaker[1].Failures.Should().Be(2,
            "auth error must not increment the counter");
        _sut.ExchangeCircuitBreaker[1].BrokenUntil.Should().Be(DateTime.MinValue,
            "circuit breaker should remain closed");
    }

    [Fact]
    public void IncrementExchangeFailure_TransientError_StillTripsNormally()
    {
        var config = new BotConfiguration
        {
            ExchangeCircuitBreakerThreshold = 2,
            ExchangeCircuitBreakerMinutes = 15,
        };

        _sut.IncrementExchangeFailure(1, config, isAuthError: false);
        _sut.IncrementExchangeFailure(1, config, isAuthError: false).Should().BeTrue(
            "non-auth failures should still trip the circuit breaker at threshold");
        _sut.ExchangeCircuitBreaker[1].BrokenUntil.Should().BeAfter(DateTime.UtcNow);
    }

    // ── IncrementAssetExchangeFailure ─────────────────────────────────────────

    [Fact]
    public void IncrementAssetExchangeFailure_TriggersCooldown_AtThreshold()
    {
        // AssetCooldownThreshold is 3
        _sut.IncrementAssetExchangeFailure(1, 10);
        _sut.IncrementAssetExchangeFailure(1, 10);
        _sut.AssetExchangeCooldowns[(1, 10)].CooldownUntil.Should().Be(DateTime.MinValue,
            "should not have cooldown below threshold");

        _sut.IncrementAssetExchangeFailure(1, 10);
        _sut.AssetExchangeCooldowns[(1, 10)].Failures.Should().Be(3);
        _sut.AssetExchangeCooldowns[(1, 10)].CooldownUntil.Should().BeAfter(DateTime.UtcNow);
    }

    // ── RecordCloseResult ─────────────────────────────────────────────────────

    [Fact]
    public void RecordCloseResult_TracksConsecutiveLosses_ResetsOnProfit()
    {
        _sut.RecordCloseResult(-5m, "user1");
        _sut.RecordCloseResult(-5m, "user1");
        _sut.RecordCloseResult(-5m, "user1");

        _sut.UserConsecutiveLosses["user1"].Should().Be(3);

        _sut.RecordCloseResult(1m, "user1");
        _sut.UserConsecutiveLosses["user1"].Should().Be(0);
    }

    [Fact]
    public void RecordCloseResult_ZeroPnl_ResetsCounter()
    {
        _sut.RecordCloseResult(-5m, "user1");
        _sut.RecordCloseResult(-5m, "user1");
        _sut.UserConsecutiveLosses["user1"].Should().Be(2);

        _sut.RecordCloseResult(0m, "user1");
        _sut.UserConsecutiveLosses["user1"].Should().Be(0, "zero PnL should reset consecutive losses");
    }

    [Fact]
    public void RecordCloseResult_IgnoresNullUserId()
    {
        _sut.RecordCloseResult(-10m, null);
        _sut.UserConsecutiveLosses.Should().BeEmpty();
    }

    [Fact]
    public void RecordCloseResult_IgnoresEmptyUserId()
    {
        _sut.RecordCloseResult(-10m, "");
        _sut.UserConsecutiveLosses.Should().BeEmpty();
    }

    // ── ClearCooldowns ────────────────────────────────────────────────────────

    [Fact]
    public void ClearCooldowns_EmptiesFailedOpCooldowns()
    {
        _sut.FailedOpCooldowns["key1"] = (DateTime.UtcNow.AddMinutes(30), 3);
        _sut.FailedOpCooldowns["key2"] = (DateTime.UtcNow.AddMinutes(10), 1);

        _sut.ClearCooldowns();

        _sut.FailedOpCooldowns.Should().BeEmpty();
    }

    // ── GetCircuitBreakerStates ───────────────────────────────────────────────

    [Fact]
    public void GetCircuitBreakerStates_ReturnsOnlyActiveBreakers()
    {
        _sut.ExchangeCircuitBreaker[1] = (3, DateTime.UtcNow.AddMinutes(10)); // active
        _sut.ExchangeCircuitBreaker[2] = (3, DateTime.UtcNow.AddMinutes(-5)); // expired
        _sut.ExchangeCircuitBreaker[3] = (1, DateTime.MinValue); // sub-threshold

        var states = _sut.GetCircuitBreakerStates();

        states.Should().HaveCount(1);
        states[0].ExchangeId.Should().Be(1);
        states[0].RemainingMinutes.Should().BeGreaterThan(0);
    }

    // ── GetCircuitBrokenExchangeIds ───────────────────────────────────────────

    [Fact]
    public void GetCircuitBrokenExchangeIds_ReturnsCorrectSet()
    {
        _sut.ExchangeCircuitBreaker[1] = (3, DateTime.UtcNow.AddMinutes(10)); // active
        _sut.ExchangeCircuitBreaker[2] = (3, DateTime.UtcNow.AddMinutes(-5)); // expired
        _sut.ExchangeCircuitBreaker[3] = (3, DateTime.UtcNow.AddMinutes(20)); // active

        var ids = _sut.GetCircuitBrokenExchangeIds();

        ids.Should().Contain(1);
        ids.Should().NotContain(2);
        ids.Should().Contain(3);
    }

    // ── Cooldown get/set/remove ───────────────────────────────────────────────

    [Fact]
    public void IsOnCooldown_ReturnsTrueWithRemaining_WhenActive()
    {
        _sut.SetCooldown("key", DateTime.UtcNow.AddMinutes(10), 1);

        var result = _sut.IsOnCooldown("key", out var remaining);

        result.Should().BeTrue();
        remaining.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void IsOnCooldown_ReturnsFalse_WhenExpired()
    {
        _sut.SetCooldown("key", DateTime.UtcNow.AddMinutes(-1), 1);

        var result = _sut.IsOnCooldown("key", out var remaining);

        result.Should().BeFalse();
        remaining.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void IsOnCooldown_ReturnsFalse_WhenNotSet()
    {
        var result = _sut.IsOnCooldown("nonexistent", out var remaining);

        result.Should().BeFalse();
        remaining.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void GetCooldownEntry_ReturnsEntry()
    {
        _sut.SetCooldown("key", DateTime.UtcNow.AddMinutes(10), 3);

        var entry = _sut.GetCooldownEntry("key");

        entry.Failures.Should().Be(3);
    }

    [Fact]
    public void RemoveCooldown_RemovesEntry()
    {
        _sut.SetCooldown("key", DateTime.UtcNow.AddMinutes(10), 1);

        _sut.RemoveCooldown("key");

        _sut.FailedOpCooldowns.Should().NotContainKey("key");
    }

    // ── Asset-exchange cooldown ───────────────────────────────────────────────

    [Fact]
    public void IsAssetExchangeOnCooldown_ReturnsTrueWhenActive()
    {
        _sut.AssetExchangeCooldowns[(1, 2)] = (3, DateTime.UtcNow.AddMinutes(10));

        _sut.IsAssetExchangeOnCooldown(1, 2).Should().BeTrue();
    }

    [Fact]
    public void IsAssetExchangeOnCooldown_ReturnsFalseWhenExpired()
    {
        _sut.AssetExchangeCooldowns[(1, 2)] = (3, DateTime.UtcNow.AddMinutes(-1));

        _sut.IsAssetExchangeOnCooldown(1, 2).Should().BeFalse();
    }

    [Fact]
    public void RemoveAssetExchangeCooldown_RemovesEntry()
    {
        _sut.AssetExchangeCooldowns[(1, 2)] = (3, DateTime.UtcNow.AddMinutes(10));

        _sut.RemoveAssetExchangeCooldown(1, 2);

        _sut.AssetExchangeCooldowns.Should().NotContainKey((1, 2));
    }

    // ── Exchange circuit breaker set/remove ───────────────────────────────────

    [Fact]
    public void SetExchangeCircuitBreaker_SetsEntry()
    {
        var brokenUntil = DateTime.UtcNow.AddMinutes(15);
        _sut.SetExchangeCircuitBreaker(5, 3, brokenUntil);

        _sut.ExchangeCircuitBreaker.Should().ContainKey(5);
        _sut.ExchangeCircuitBreaker[5].Failures.Should().Be(3);
        _sut.ExchangeCircuitBreaker[5].BrokenUntil.Should().Be(brokenUntil);
    }

    [Fact]
    public void RemoveExchangeCircuitBreaker_RemovesEntry()
    {
        _sut.ExchangeCircuitBreaker[5] = (3, DateTime.UtcNow.AddMinutes(15));

        _sut.RemoveExchangeCircuitBreaker(5);

        _sut.ExchangeCircuitBreaker.Should().NotContainKey(5);
    }

    // ── Rotation cooldown and daily rotation count ────────────────────────────

    [Fact]
    public void GetRotationCooldown_ReturnsNull_WhenNotSet()
    {
        _sut.GetRotationCooldown("nonexistent").Should().BeNull();
    }

    [Fact]
    public void SetRotationCooldown_SetsAndGets()
    {
        var until = DateTime.UtcNow.AddMinutes(5);
        _sut.SetRotationCooldown("key", until);

        _sut.GetRotationCooldown("key").Should().Be(until);
    }

    [Fact]
    public void GetDailyRotationCount_ReturnsDefaultForNewUser()
    {
        var (date, count) = _sut.GetDailyRotationCount("newuser");

        date.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow));
        count.Should().Be(0);
    }

    [Fact]
    public void SetDailyRotationCount_SetsAndGets()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _sut.SetDailyRotationCount("user1", today, 3);

        var (date, count) = _sut.GetDailyRotationCount("user1");
        date.Should().Be(today);
        count.Should().Be(3);
    }

    // ── GetConsecutiveLosses ──────────────────────────────────────────────────

    [Fact]
    public void GetConsecutiveLosses_ReturnsZeroForNewUser()
    {
        _sut.GetConsecutiveLosses("unknown").Should().Be(0);
    }

    [Fact]
    public void GetConsecutiveLosses_ReturnsTrackedCount()
    {
        _sut.RecordCloseResult(-1m, "user1");
        _sut.RecordCloseResult(-1m, "user1");

        _sut.GetConsecutiveLosses("user1").Should().Be(2);
    }

    // ── PnlTarget cooldown ────────────────────────────────────────────────────

    [Fact]
    public void SetCooldown_PnlTargetReason_UsesConfiguredDuration()
    {
        const string key = "user1:BTC:exchange1:exchange2";

        // Active cooldown — IsOnCooldown must return true
        _sut.SetCooldown(key, DateTime.UtcNow.AddMinutes(30), 0);
        _sut.IsOnCooldown(key, out var remaining).Should().BeTrue(
            "cooldown should be active immediately after SetCooldown with a future expiry");
        remaining.Should().BeGreaterThan(TimeSpan.Zero);

        // Simulate expiry by writing a past time; IsOnCooldown must return false
        _sut.SetCooldown(key, DateTime.UtcNow.AddMinutes(-1), 0);
        _sut.IsOnCooldown(key, out var remainingAfterExpiry).Should().BeFalse(
            "cooldown should not suppress re-entry once the expiry time has passed");
        remainingAfterExpiry.Should().Be(TimeSpan.Zero);
    }
}
