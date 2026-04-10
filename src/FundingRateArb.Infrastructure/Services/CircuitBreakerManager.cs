using System.Collections.Concurrent;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.Services;

public class CircuitBreakerManager : ICircuitBreakerManager
{
    private readonly ILogger<CircuitBreakerManager> _logger;

    // Per-user consecutive loss tracking for circuit breaker
    private readonly ConcurrentDictionary<string, int> _userConsecutiveLosses = new();

    // Per-user cooldown for failed opportunities — keyed by (userId, oppKey)
    private readonly ConcurrentDictionary<string, (DateTime CooldownUntil, int Failures)> _failedOpCooldowns = new();
    internal static readonly TimeSpan BaseCooldown = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(60);

    // Per-exchange circuit breaker — keyed by exchangeId
    private readonly ConcurrentDictionary<int, (int Failures, DateTime BrokenUntil)> _exchangeCircuitBreaker = new();

    // Per-asset per-exchange cooldown — prevents repeated open failures for same asset+exchange
    private readonly ConcurrentDictionary<(int AssetId, int ExchangeId), (int Failures, DateTime CooldownUntil)> _assetExchangeCooldowns = new();
    private const int AssetCooldownThreshold = 3;
    private static readonly TimeSpan AssetCooldownDuration = TimeSpan.FromMinutes(10);

    // Per-opportunity cooldown after rotation — prevents flip-flopping
    private readonly ConcurrentDictionary<string, DateTime> _rotationCooldowns = new();
    internal static readonly TimeSpan RotationCooldownDuration = TimeSpan.FromMinutes(5);

    // Track daily rotation count per user
    private readonly ConcurrentDictionary<string, (DateOnly Date, int Count)> _dailyRotationCounts = new();

    /// <summary>Exposes asset-exchange cooldown state for unit testing.</summary>
    internal ConcurrentDictionary<(int AssetId, int ExchangeId), (int Failures, DateTime CooldownUntil)> AssetExchangeCooldowns => _assetExchangeCooldowns;

    /// <summary>Exposes cooldown state for unit testing.</summary>
    internal ConcurrentDictionary<string, (DateTime CooldownUntil, int Failures)> FailedOpCooldowns => _failedOpCooldowns;

    /// <summary>Exposes per-user consecutive loss counts for unit testing.</summary>
    internal ConcurrentDictionary<string, int> UserConsecutiveLosses => _userConsecutiveLosses;

    /// <summary>Exposes exchange circuit breaker state for unit testing.</summary>
    internal ConcurrentDictionary<int, (int Failures, DateTime BrokenUntil)> ExchangeCircuitBreaker => _exchangeCircuitBreaker;

    /// <summary>Exposes rotation cooldowns for unit testing.</summary>
    internal ConcurrentDictionary<string, DateTime> RotationCooldowns => _rotationCooldowns;

    /// <summary>Exposes daily rotation counts for unit testing.</summary>
    internal ConcurrentDictionary<string, (DateOnly Date, int Count)> DailyRotationCounts => _dailyRotationCounts;

    public TimeSpan BaseCooldownDuration => BaseCooldown;
    public TimeSpan MaxCooldownDuration => MaxCooldown;
    TimeSpan ICircuitBreakerManager.RotationCooldownDuration => RotationCooldownDuration;

    public CircuitBreakerManager(ILogger<CircuitBreakerManager> logger)
    {
        _logger = logger;
    }

    public void SweepExpiredEntries()
    {
        // Sweep expired circuit breaker entries. Sub-threshold entries (BrokenUntil == DateTime.MinValue)
        // are not swept here — they are bounded by the finite set of exchange IDs and acceptable.
        var expiredCbKeys = _exchangeCircuitBreaker
            .Where(kvp => kvp.Value.BrokenUntil < DateTime.UtcNow && kvp.Value.BrokenUntil != DateTime.MinValue)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in expiredCbKeys)
        {
            _exchangeCircuitBreaker.TryRemove(key, out _);
        }

        // Sweep stale per-opportunity cooldown entries to prevent unbounded dictionary growth.
        // Only remove entries that expired more than MaxCooldown ago, preserving failure counts
        // for recently-expired entries so exponential backoff continues correctly on retry.
        var cooldownSweepThreshold = DateTime.UtcNow - MaxCooldown;
        var expiredCooldownKeys = _failedOpCooldowns
            .Where(kvp => kvp.Value.CooldownUntil < cooldownSweepThreshold)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in expiredCooldownKeys)
        {
            _failedOpCooldowns.TryRemove(key, out _);
        }

        // Sweep expired asset-exchange cooldowns.
        // Pre-threshold entries (CooldownUntil == DateTime.MinValue) are bounded by
        // the finite set of (asset, exchange) pairs and cleaned on successful opens.
        if (!_assetExchangeCooldowns.IsEmpty)
        {
            var now = DateTime.UtcNow;
            var expiredAssetCooldowns = _assetExchangeCooldowns
                .Where(kvp => kvp.Value.CooldownUntil != DateTime.MinValue && kvp.Value.CooldownUntil < now)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in expiredAssetCooldowns)
            {
                _assetExchangeCooldowns.TryRemove(key, out _);
            }
        }

        // Sweep expired rotation cooldowns
        if (!_rotationCooldowns.IsEmpty)
        {
            var now = DateTime.UtcNow;
            var expiredRotations = _rotationCooldowns
                .Where(kvp => kvp.Value < now)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in expiredRotations)
            {
                _rotationCooldowns.TryRemove(key, out _);
            }
        }
    }

    public void IncrementExchangeFailure(int exchangeId, BotConfiguration config)
    {
        var threshold = config.ExchangeCircuitBreakerThreshold;
        var brokenUntil = DateTime.UtcNow.AddMinutes(config.ExchangeCircuitBreakerMinutes);

        var updated = _exchangeCircuitBreaker.AddOrUpdate(
            exchangeId,
            _ =>
            {
                var f = 1;
                return (f, f >= threshold ? brokenUntil : DateTime.MinValue);
            },
            (_, current) =>
            {
                var f = current.Failures + 1;
                return (f, f >= threshold ? brokenUntil : DateTime.MinValue);
            });

        if (updated.Failures >= threshold)
        {
            _logger.LogWarning(
                "Circuit breaker OPEN for exchange {ExchangeId}: {Failures} consecutive failures, excluded for {Minutes}m",
                exchangeId, updated.Failures, config.ExchangeCircuitBreakerMinutes);
        }
    }

    public void IncrementAssetExchangeFailure(int assetId, int exchangeId)
    {
        var now = DateTime.UtcNow;
        var cooldownUntil = now.Add(AssetCooldownDuration);
        _assetExchangeCooldowns.AddOrUpdate(
            (assetId, exchangeId),
            _ => (1, 1 >= AssetCooldownThreshold ? cooldownUntil : DateTime.MinValue),
            (_, current) =>
            {
                var f = current.Failures + 1;
                return (f, f >= AssetCooldownThreshold ? cooldownUntil : current.CooldownUntil);
            });
    }

    public void RecordCloseResult(decimal realizedPnl, string? userId = null)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return;
        }

        if (realizedPnl < 0)
        {
            _userConsecutiveLosses.AddOrUpdate(userId, 1, (_, count) => count + 1);
        }
        else
        {
            _userConsecutiveLosses[userId] = 0;
        }
    }

    public IReadOnlyList<CircuitBreakerStatusDto> GetCircuitBreakerStates()
    {
        var now = DateTime.UtcNow;
        return _exchangeCircuitBreaker
            .Where(kvp => kvp.Value.BrokenUntil > now)
            .Select(kvp => new CircuitBreakerStatusDto
            {
                ExchangeId = kvp.Key,
                ExchangeName = $"Exchange-{kvp.Key}",
                BrokenUntil = kvp.Value.BrokenUntil,
                RemainingMinutes = (int)Math.Ceiling((kvp.Value.BrokenUntil - now).TotalMinutes)
            })
            .ToList();
    }

    public IReadOnlyList<ActiveCooldownDto> GetActivePairCooldowns()
    {
        var now = DateTime.UtcNow;
        return _failedOpCooldowns
            .Where(kvp => kvp.Value.CooldownUntil > now)
            .Select(kvp => new ActiveCooldownDto
            {
                CooldownKey = kvp.Key,
                ExpiresAt = kvp.Value.CooldownUntil,
                RemainingMinutes = (int)Math.Ceiling((kvp.Value.CooldownUntil - now).TotalMinutes),
            })
            .OrderBy(c => c.ExpiresAt)
            .ToList();
    }

    public void ClearCooldowns() => _failedOpCooldowns.Clear();

    public HashSet<int> GetCircuitBrokenExchangeIds()
    {
        return _exchangeCircuitBreaker
            .Where(kvp => kvp.Value.BrokenUntil > DateTime.UtcNow)
            .Select(kvp => kvp.Key)
            .ToHashSet();
    }

    public bool IsOnCooldown(string cooldownKey, out TimeSpan remaining)
    {
        if (_failedOpCooldowns.TryGetValue(cooldownKey, out var cd) && DateTime.UtcNow < cd.CooldownUntil)
        {
            remaining = cd.CooldownUntil - DateTime.UtcNow;
            return true;
        }
        remaining = TimeSpan.Zero;
        return false;
    }

    public DateTime? GetCooldownUntil(string cooldownKey)
    {
        if (_failedOpCooldowns.TryGetValue(cooldownKey, out var cd) && DateTime.UtcNow < cd.CooldownUntil)
        {
            return cd.CooldownUntil;
        }
        return null;
    }

    public (DateTime CooldownUntil, int Failures) GetCooldownEntry(string cooldownKey)
    {
        return _failedOpCooldowns.GetValueOrDefault(cooldownKey);
    }

    public void SetCooldown(string key, DateTime cooldownUntil, int failures)
    {
        _failedOpCooldowns[key] = (cooldownUntil, failures);
    }

    public void RemoveCooldown(string key)
    {
        _failedOpCooldowns.TryRemove(key, out _);
    }

    public bool IsAssetExchangeOnCooldown(int assetId, int exchangeId)
    {
        return _assetExchangeCooldowns.TryGetValue((assetId, exchangeId), out var ac)
               && ac.CooldownUntil > DateTime.UtcNow;
    }

    public void RemoveAssetExchangeCooldown(int assetId, int exchangeId)
    {
        _assetExchangeCooldowns.TryRemove((assetId, exchangeId), out _);
    }

    public void SetExchangeCircuitBreaker(int exchangeId, int failures, DateTime brokenUntil)
    {
        _exchangeCircuitBreaker[exchangeId] = (failures, brokenUntil);
    }

    public void RemoveExchangeCircuitBreaker(int exchangeId)
    {
        _exchangeCircuitBreaker.TryRemove(exchangeId, out _);
    }

    public DateTime? GetRotationCooldown(string key)
    {
        return _rotationCooldowns.TryGetValue(key, out var until) ? until : null;
    }

    public void SetRotationCooldown(string key, DateTime until)
    {
        _rotationCooldowns[key] = until;
    }

    public (DateOnly Date, int Count) GetDailyRotationCount(string userId)
    {
        return _dailyRotationCounts.GetValueOrDefault(userId, (DateOnly.FromDateTime(DateTime.UtcNow), 0));
    }

    public void SetDailyRotationCount(string userId, DateOnly date, int count)
    {
        _dailyRotationCounts[userId] = (date, count);
    }

    public int GetConsecutiveLosses(string userId)
    {
        return _userConsecutiveLosses.GetValueOrDefault(userId, 0);
    }
}
