using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Common.Repositories;

public interface IPositionRepository
{
    Task<ArbitragePosition?> GetByIdAsync(int id);
    Task<List<ArbitragePosition>> GetOpenAsync();
    Task<List<ArbitragePosition>> GetByUserAsync(string userId);
    Task<List<ArbitragePosition>> GetAllAsync();
    void Add(ArbitragePosition position);
    void Update(ArbitragePosition position);
}
