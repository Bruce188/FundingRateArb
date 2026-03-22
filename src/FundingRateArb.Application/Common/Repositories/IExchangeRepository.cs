using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Common.Repositories;

public interface IExchangeRepository
{
    Task<List<Exchange>> GetAllAsync();
    Task<List<Exchange>> GetActiveAsync();
    Task<Exchange?> GetByIdAsync(int id);
    Task<Exchange?> GetByNameAsync(string name);
    void Add(Exchange exchange);
    void Update(Exchange exchange);
    void Remove(Exchange exchange);
    void InvalidateCache();
}
