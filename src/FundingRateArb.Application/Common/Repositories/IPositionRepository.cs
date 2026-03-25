using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Common.Repositories;

public interface IPositionRepository
{
    Task<ArbitragePosition?> GetByIdAsync(int id);

    /// <summary>Returns open positions without change tracking (read-only).</summary>
    Task<List<ArbitragePosition>> GetOpenAsync();

    /// <summary>Returns open positions for a specific user without change tracking.</summary>
    Task<List<ArbitragePosition>> GetOpenByUserAsync(string userId);

    /// <summary>Returns open positions with EF change tracking so mutations are persisted.</summary>
    Task<List<ArbitragePosition>> GetOpenTrackedAsync();

    Task<List<ArbitragePosition>> GetByUserAsync(string userId, int skip = 0, int take = 500);
    Task<List<ArbitragePosition>> GetAllAsync(int skip = 0, int take = 500);
    Task<List<ArbitragePosition>> GetByStatusAsync(PositionStatus status);
    Task<List<ArbitragePosition>> GetClosedSinceAsync(DateTime since);

    /// <summary>Returns closed positions with navigation properties (Asset, LongExchange, ShortExchange) loaded.
    /// Use for analytics that need GroupBy on navigation properties.</summary>
    Task<List<ArbitragePosition>> GetClosedWithNavigationSinceAsync(DateTime since, string? userId = null, CancellationToken ct = default);

    void Add(ArbitragePosition position);
    void Update(ArbitragePosition position);
}
