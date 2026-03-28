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

        foreach (var cred in credentials)
        {
            var exchangeName = cred.Exchange?.Name;
            if (string.IsNullOrEmpty(exchangeName))
            {
                _logger.LogWarning("Credential {CredId} has no exchange name, skipping", cred.Id);
                continue;
            }

            var decrypted = _userSettings.DecryptCredential(cred);
            var connector = await _connectorFactory.CreateForUserAsync(
                exchangeName, decrypted.ApiKey, decrypted.ApiSecret,
                decrypted.WalletAddress, decrypted.PrivateKey,
                decrypted.SubAccountAddress, decrypted.ApiKeyIndex);

            if (connector is null)
            {
                _logger.LogWarning("Could not create connector for {Exchange} (user {UserId})", exchangeName, userId);
                balanceTasks.Add((cred.ExchangeId, exchangeName, Task.FromException<decimal>(
                    new InvalidOperationException(CredentialsNotConfiguredMessage))));
                continue;
            }

            balanceTasks.Add((cred.ExchangeId, exchangeName, connector.GetAvailableBalanceAsync(ct)));
        }

        var now = DateTime.UtcNow;
        var balances = new List<ExchangeBalanceDto>();

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
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch balance from {Exchange} for user {UserId}, using 0",
                    exchangeName, userId);
                balances.Add(new ExchangeBalanceDto
                {
                    ExchangeId = exchangeId,
                    ExchangeName = exchangeName,
                    AvailableUsdc = 0m,
                    ErrorMessage = SanitizeErrorMessage(ex),
                    FetchedAt = now,
                });
            }
        }

        var snapshot = new BalanceSnapshotDto
        {
            Balances = balances,
            TotalAvailableUsdc = balances.Sum(b => b.AvailableUsdc),
            FetchedAt = now,
        };

        _cache.Set(cacheKey, snapshot, CacheTtl);
        return snapshot;
    }

    private static string SanitizeErrorMessage(Exception ex) => ex switch
    {
        HttpRequestException => "Exchange unreachable",
        InvalidOperationException when ex.Message == CredentialsNotConfiguredMessage => CredentialsNotConfiguredMessage,
        _ => "Balance fetch failed",
    };
}
