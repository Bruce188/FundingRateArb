using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.Services;

public class CapitalProvider : ICapitalProvider
{
    private const string CacheKey = "cp:evaluated-capital:v1";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly IUnitOfWork _uow;
    private readonly IBalanceAggregator _balanceAggregator;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CapitalProvider>? _logger;

    public CapitalProvider(
        IUnitOfWork uow,
        IBalanceAggregator balanceAggregator,
        IMemoryCache cache,
        ILogger<CapitalProvider>? logger = null)
    {
        _uow = uow;
        _balanceAggregator = balanceAggregator;
        _cache = cache;
        _logger = logger;
    }

    public async Task<decimal> GetEvaluatedCapitalUsdcAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey, out decimal cached))
        {
            return cached;
        }

        decimal evaluated;
        try
        {
#pragma warning disable CS0618
            var config = await _uow.BotConfig.GetActiveAsync();
            var configCap = config.TotalCapitalUsdc;
#pragma warning restore CS0618

            var userIds = await _uow.UserConfigurations.GetAllEnabledUserIdsAsync();
            var totalLive = 0m;
            var anyBalance = false;

            foreach (var userId in userIds)
            {
                try
                {
                    var snapshot = await _balanceAggregator.GetBalanceSnapshotAsync(userId, ct);
                    var userBalance = snapshot.Balances.Sum(dto =>
                        dto.IsUnavailable ? 0m :
                        dto.IsFallbackEligible ? dto.LastKnownAvailableUsdc!.Value :
                        dto.AvailableUsdc);
                    if (userBalance > 0)
                    {
                        totalLive += userBalance;
                        anyBalance = true;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogWarning(ex,
                        "CapitalProvider: failed to fetch balance for user {UserId}; skipping", userId);
                }
            }

            evaluated = anyBalance
                ? Math.Min(totalLive, configCap)
                : configCap;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex,
                "CapitalProvider: failed to resolve live capital; falling back to config.TotalCapitalUsdc");
            try
            {
#pragma warning disable CS0618
                evaluated = (await _uow.BotConfig.GetActiveAsync()).TotalCapitalUsdc;
#pragma warning restore CS0618
            }
            catch
            {
                evaluated = 0m;
            }
        }

        _cache.Set(CacheKey, evaluated, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheTtl
        });

        return evaluated;
    }
}
