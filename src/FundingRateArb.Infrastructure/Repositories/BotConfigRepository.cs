using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FundingRateArb.Infrastructure.Repositories;

public class BotConfigRepository : IBotConfigRepository
{
    private const string CacheKey = "BotConfig:Active";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _context;
    private readonly IMemoryCache _cache;

    public BotConfigRepository(AppDbContext context, IMemoryCache cache)
    {
        _context = context;
        _cache = cache;
    }

    public async Task<BotConfiguration> GetActiveAsync()
    {
        if (_cache.TryGetValue(CacheKey, out BotConfiguration? cached) && cached is not null)
        {
            return ShallowCopy(cached);
        }

        var config = await _context.BotConfigurations.AsNoTracking().FirstOrDefaultAsync();
        if (config is null)
        {
            throw new InvalidOperationException(
                "No BotConfiguration found. Run the seeder or create one via Admin UI.");
        }

        _cache.Set(CacheKey, config, new MemoryCacheEntryOptions
        {
            SlidingExpiration = CacheDuration,
        });

        return ShallowCopy(config);
    }

    public async Task<BotConfiguration> GetActiveTrackedAsync()
    {
        var config = await _context.BotConfigurations.FirstOrDefaultAsync();
        if (config is null)
        {
            throw new InvalidOperationException(
                "No BotConfiguration found. Run the seeder or create one via Admin UI.");
        }

        return config;
    }

    public void Update(BotConfiguration config) =>
        _context.BotConfigurations.Update(config);

    public void InvalidateCache() => _cache.Remove(CacheKey);

    private static BotConfiguration ShallowCopy(BotConfiguration src) => new()
    {
        Id = src.Id,
        IsEnabled = src.IsEnabled,
        OpenThreshold = src.OpenThreshold,
        AlertThreshold = src.AlertThreshold,
        CloseThreshold = src.CloseThreshold,
        StopLossPct = src.StopLossPct,
        MaxHoldTimeHours = src.MaxHoldTimeHours,
        VolumeFraction = src.VolumeFraction,
        MaxCapitalPerPosition = src.MaxCapitalPerPosition,
        BreakevenHoursMax = src.BreakevenHoursMax,
        TotalCapitalUsdc = src.TotalCapitalUsdc,
        DefaultLeverage = src.DefaultLeverage,
        MaxConcurrentPositions = src.MaxConcurrentPositions,
        AllocationStrategy = src.AllocationStrategy,
        AllocationTopN = src.AllocationTopN,
        FeeAmortizationHours = src.FeeAmortizationHours,
        MinPositionSizeUsdc = src.MinPositionSizeUsdc,
        MinVolume24hUsdc = src.MinVolume24hUsdc,
        RateStalenessMinutes = src.RateStalenessMinutes,
        DailyDrawdownPausePct = src.DailyDrawdownPausePct,
        ConsecutiveLossPause = src.ConsecutiveLossPause,
        FundingWindowMinutes = src.FundingWindowMinutes,
        MaxExposurePerAsset = src.MaxExposurePerAsset,
        MaxExposurePerExchange = src.MaxExposurePerExchange,
        TargetPnlMultiplier = src.TargetPnlMultiplier,
        AdaptiveHoldEnabled = src.AdaptiveHoldEnabled,
        RebalanceEnabled = src.RebalanceEnabled,
        RebalanceMinImprovement = src.RebalanceMinImprovement,
        MaxRebalancesPerCycle = src.MaxRebalancesPerCycle,
        LastUpdatedAt = src.LastUpdatedAt,
        UpdatedByUserId = src.UpdatedByUserId,
    };
}
