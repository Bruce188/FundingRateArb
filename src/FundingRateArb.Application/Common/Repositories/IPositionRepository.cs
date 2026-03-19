using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Common.Repositories;

public interface IPositionRepository
{
    Task<ArbitragePosition?> GetByIdAsync(int id);

    /// <summary>Returns open positions without change tracking (read-only).</summary>
    Task<List<ArbitragePosition>> GetOpenAsync();

    /// <summary>Returns open positions with EF change tracking so mutations are persisted.</summary>
    Task<List<ArbitragePosition>> GetOpenTrackedAsync();

    Task<List<ArbitragePosition>> GetByUserAsync(string userId, int skip = 0, int take = 500);
    Task<List<ArbitragePosition>> GetAllAsync(int skip = 0, int take = 500);
    void Add(ArbitragePosition position);
    void Update(ArbitragePosition position);
}
