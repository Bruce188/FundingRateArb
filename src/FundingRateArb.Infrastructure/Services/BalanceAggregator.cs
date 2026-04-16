using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.Services;

public class BalanceAggregator : IBalanceAggregator
{
    internal const string CredentialsNotConfiguredMessage = "Credentials not configured";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    internal static TimeSpan GetAuthErrorBackoff(int consecutiveFailures) =>
        TimeSpan.FromMinutes(Math.Min(5 * Math.Pow(2, Math.Max(0, consecutiveFailures - 1)), 60));

    private readonly IUserSettingsService _userSettings;
    private readonly IExchangeConnectorFactory _connectorFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<BalanceAggregator> _logger;

    public BalanceAggregator(
        IUserSettingsService userSettings,
        IExchangeConnectorFactory connectorFactory,
        IMemoryCache cache,
        ILogger<BalanceAggregator> logger)
    {
        _userSettings = userSettings;
        _connectorFactory = connectorFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<BalanceSnapshotDto> GetBalanceSnapshotAsync(string userId, CancellationToken ct = default)
    {
        var cacheKey = $"balance:{userId}";

        if (_cache.TryGetValue<BalanceSnapshotDto>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var credentials = await _userSettings.GetActiveCredentialsAsync(userId);

        if (credentials.Count == 0)
        {
            var empty = new BalanceSnapshotDto { FetchedAt = DateTime.UtcNow };
            _cache.Set(cacheKey, empty, CacheTtl);
            return empty;
        }

        var balanceTasks = new List<(int ExchangeId, string ExchangeName, Task<decimal> BalanceTask)>();
        var balances = new List<ExchangeBalanceDto>();

        foreach (var cred in credentials)
        {
            var exchangeName = cred.Exchange?.Name;
            if (string.IsNullOrEmpty(exchangeName))
            {
                _logger.LogWarning("Credential {CredId} has no exchange name, skipping", cred.Id);
                continue;
            }

            // Skip credentials in auth-error backoff
            if (cred.LastError is not null
                && cred.LastErrorAt is not null
                && IsAuthError(cred.LastError)
                && DateTime.UtcNow - cred.LastErrorAt.Value < GetAuthErrorBackoff(cred.ConsecutiveFailures))
            {
                balances.Add(new ExchangeBalanceDto
                {
                    ExchangeId = cred.ExchangeId,
                    ExchangeName = exchangeName,
                    AvailableUsdc = 0m,
                    ErrorMessage = cred.LastError,
                    FetchedAt = DateTime.UtcNow,
                    IsUnavailable = true,
                });
                continue;
            }

            var decrypted = _userSettings.DecryptCredential(cred);
            var connector = await _connectorFactory.CreateForUserAsync(
                exchangeName, decrypted.ApiKey, decrypted.ApiSecret,
                decrypted.WalletAddress, decrypted.PrivateKey,
                decrypted.SubAccountAddress, decrypted.ApiKeyIndex);

            if (connector is null)
            {
                var suppressKey = $"connector-warn:{userId}:{exchangeName}";
                if (!_cache.TryGetValue(suppressKey, out _))
                {
                    _logger.LogWarning("Could not create connector for {Exchange} (user {UserId}) — check credential configuration", exchangeName, userId);
                    _cache.Set(suppressKey, true, TimeSpan.FromMinutes(15));
                }

                balances.Add(new ExchangeBalanceDto
                {
                    ExchangeId = cred.ExchangeId,
                    ExchangeName = exchangeName,
                    AvailableUsdc = 0m,
                    ErrorMessage = CredentialsNotConfiguredMessage,
                    FetchedAt = DateTime.UtcNow,
                    IsUnavailable = true,
                });
                continue;
            }

            balanceTasks.Add((cred.ExchangeId, exchangeName, connector.GetAvailableBalanceAsync(ct)));
        }

        var now = DateTime.UtcNow;

        // Await all balance tasks, handling individual failures
        foreach (var (exchangeId, exchangeName, task) in balanceTasks)
        {
            try
            {
                var balance = await task;
                balances.Add(new ExchangeBalanceDto
                {
                    ExchangeId = exchangeId,
                    ExchangeName = exchangeName,
                    AvailableUsdc = balance,
                    FetchedAt = now,
                });

                // Write last-known-good cache for transient error fallback
                _cache.Set($"balance-lkg:{userId}:{exchangeId}", (Value: balance, FetchedAt: now), TimeSpan.FromHours(1));

                // Clear credential error on success
                await _userSettings.UpdateCredentialErrorAsync(userId, exchangeId, null, ct);
            }
            catch (Exception ex)
            {
                var sanitized = SanitizeErrorMessage(ex, exchangeName);

                if (IsAuthError(sanitized))
                {
                    // Credential error: mark exchange unavailable, disable trading
                    _logger.LogWarning("{Exchange} credentials invalid for user {UserId}, trading disabled for this exchange", exchangeName, userId);
                    balances.Add(new ExchangeBalanceDto
                    {
                        ExchangeId = exchangeId,
                        ExchangeName = exchangeName,
                        AvailableUsdc = 0m,
                        ErrorMessage = sanitized,
                        FetchedAt = now,
                        IsUnavailable = true,
                    });

                    // Persist auth error to credential for backoff tracking
                    await _userSettings.UpdateCredentialErrorAsync(userId, exchangeId, sanitized, ct);
                }
                else
                {
                    // Transient error: fall back to last-known-good balance
                    var lkgKey = $"balance-lkg:{userId}:{exchangeId}";
                    if (_cache.TryGetValue<(decimal Value, DateTime FetchedAt)>(lkgKey, out var lkg))
                    {
                        var isStale = now - lkg.FetchedAt > TimeSpan.FromMinutes(10);
                        _logger.LogWarning(ex, "Transient balance fetch error for {Exchange}, user {UserId} — using last-known-good",
                            exchangeName, userId);
                        balances.Add(new ExchangeBalanceDto
                        {
                            ExchangeId = exchangeId,
                            ExchangeName = exchangeName,
                            AvailableUsdc = lkg.Value,
                            ErrorMessage = $"{exchangeName}: using cached balance",
                            FetchedAt = now,
                            IsStale = isStale,
                        });
                    }
                    else
                    {
                        _logger.LogWarning(ex, "Transient balance fetch error for {Exchange}, user {UserId} — no cached balance available",
                            exchangeName, userId);
                        balances.Add(new ExchangeBalanceDto
                        {
                            ExchangeId = exchangeId,
                            ExchangeName = exchangeName,
                            AvailableUsdc = 0m,
                            ErrorMessage = sanitized,
                            FetchedAt = now,
                            IsUnavailable = true,
                        });
                    }
                }
            }
        }

        var snapshot = new BalanceSnapshotDto
        {
            Balances = balances,
            TotalAvailableUsdc = balances.Where(b => !b.IsUnavailable).Sum(b => b.AvailableUsdc),
            FetchedAt = now,
        };

        _cache.Set(cacheKey, snapshot, CacheTtl);
        return snapshot;
    }

    private static string SanitizeErrorMessage(Exception ex, string exchangeName) => ex switch
    {
        HttpRequestException => $"{exchangeName}: unreachable",
        InvalidOperationException when ex.Message == CredentialsNotConfiguredMessage => CredentialsNotConfiguredMessage,
        InvalidOperationException when ex.Message.Contains("Invalid API-key", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("-2015", StringComparison.Ordinal)
            || ex.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
            => $"{exchangeName}: API key invalid or expired",
        InvalidOperationException when ex.Message.Contains("No recognized quote asset", StringComparison.Ordinal)
            => $"{exchangeName}: no recognized quote asset (USDT/USDC/USD) found",
        ArgumentException when ex.Message.Contains("credentials", StringComparison.OrdinalIgnoreCase)
            => $"{exchangeName}: API key invalid or expired",
        _ => $"{exchangeName}: balance fetch failed",
    };

    private static bool IsAuthError(string? sanitizedMessage) =>
        sanitizedMessage is not null
        && sanitizedMessage.Contains("API key invalid", StringComparison.OrdinalIgnoreCase);
}
