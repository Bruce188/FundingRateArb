using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Common.Repositories;

public interface IUserExchangeCredentialRepository
{
    Task<UserExchangeCredential?> GetByUserAndExchangeAsync(string userId, int exchangeId);
    Task<List<UserExchangeCredential>> GetActiveByUserAsync(string userId);
    Task<List<UserExchangeCredential>> GetAllByUserAsync(string userId);
    Task<List<string>> GetDistinctUserIdsByExchangeNameAsync(string exchangeName, CancellationToken ct = default);
    void Add(UserExchangeCredential credential);
    void Update(UserExchangeCredential credential);
    void Delete(UserExchangeCredential credential);
}
