using FundingRateArb.Application.DTOs;
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

    /// <summary>Returns a count of positions with the given status via SQL COUNT(*), without materializing entities.</summary>
    Task<int> CountByStatusAsync(PositionStatus status);
    Task<List<ArbitragePosition>> GetByStatusesAsync(params PositionStatus[] statuses);

    /// <summary>Returns positions for a specific user with the given statuses. Pushes userId filter into SQL.</summary>
    Task<List<ArbitragePosition>> GetByUserAndStatusesAsync(string userId, params PositionStatus[] statuses);
    Task<List<ArbitragePosition>> GetClosedSinceAsync(DateTime since);

    /// <summary>Returns closed positions with navigation properties (Asset, LongExchange, ShortExchange) loaded.
    /// Use for analytics that need GroupBy on navigation properties.
    /// The maxRows parameter limits the SQL query result set size to prevent unbounded memory usage.</summary>
    Task<List<ArbitragePosition>> GetClosedWithNavigationSinceAsync(DateTime since, string? userId = null, int maxRows = 10_000, CancellationToken ct = default);

    /// <summary>Returns a lightweight projection of closed positions for KPI computation.
    /// Projects only scalar fields needed for aggregation, avoiding full entity graph materialization.</summary>
    Task<List<ClosedPositionKpiDto>> GetClosedKpiProjectionSinceAsync(DateTime since, string? userId = null, int maxRows = 10_000, CancellationToken ct = default);

    /// <summary>Returns pre-computed scalar KPIs (total PnL, win count, best/worst, hold time)
    /// via SQL aggregation. Includes 7d and 30d PnL windows. No row materialization.</summary>
    Task<KpiAggregateDto> GetKpiAggregatesAsync(DateTime since, string? userId = null, CancellationToken ct = default);

    /// <summary>Returns per-asset KPI breakdown via SQL GROUP BY. No row materialization.</summary>
    Task<List<AssetKpiAggregateDto>> GetPerAssetKpiAsync(DateTime since, string? userId = null, CancellationToken ct = default);

    /// <summary>Returns per-exchange-pair KPI breakdown via SQL GROUP BY. No row materialization.</summary>
    Task<List<ExchangePairKpiAggregateDto>> GetPerExchangePairKpiAsync(DateTime since, string? userId = null, CancellationToken ct = default);

    void Add(ArbitragePosition position);
    void Update(ArbitragePosition position);
}
