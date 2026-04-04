using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Interfaces;

/// <summary>
/// Manages circuit breaker state, cooldown tracking, and failure management.
/// Singleton — owns all ConcurrentDictionary state extracted from BotOrchestrator.
/// </summary>
public interface ICircuitBreakerManager
{
    void SweepExpiredEntries();
    void IncrementExchangeFailure(int exchangeId, BotConfiguration config);
    void IncrementAssetExchangeFailure(int assetId, int exchangeId);
    void RecordCloseResult(decimal realizedPnl, string? userId);
    IReadOnlyList<CircuitBreakerStatusDto> GetCircuitBreakerStates();
    void ClearCooldowns();
    HashSet<int> GetCircuitBrokenExchangeIds();
    bool IsOnCooldown(string cooldownKey, out TimeSpan remaining);
    (DateTime CooldownUntil, int Failures) GetCooldownEntry(string cooldownKey);
    void SetCooldown(string key, DateTime cooldownUntil, int failures);
    void RemoveCooldown(string key);
    bool IsAssetExchangeOnCooldown(int assetId, int exchangeId);
    void RemoveAssetExchangeCooldown(int assetId, int exchangeId);
    void SetExchangeCircuitBreaker(int exchangeId, int failures, DateTime brokenUntil);
    void RemoveExchangeCircuitBreaker(int exchangeId);
    DateTime? GetRotationCooldown(string key);
    void SetRotationCooldown(string key, DateTime until);
    (DateOnly Date, int Count) GetDailyRotationCount(string userId);
    void SetDailyRotationCount(string userId, DateOnly date, int count);
    int GetConsecutiveLosses(string userId);
}
