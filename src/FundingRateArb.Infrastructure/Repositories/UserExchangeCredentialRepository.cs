using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FundingRateArb.Infrastructure.Repositories;

public class UserExchangeCredentialRepository : IUserExchangeCredentialRepository
{
    private readonly AppDbContext _context;

    public UserExchangeCredentialRepository(AppDbContext context) => _context = context;

    public Task<UserExchangeCredential?> GetByUserAndExchangeAsync(string userId, int exchangeId) =>
        _context.UserExchangeCredentials
            .Include(c => c.Exchange)
            .FirstOrDefaultAsync(c => c.UserId == userId && c.ExchangeId == exchangeId);

    public Task<List<UserExchangeCredential>> GetActiveByUserAsync(string userId) =>
        _context.UserExchangeCredentials
            .Include(c => c.Exchange)
            .Where(c => c.UserId == userId && c.IsActive)
            .ToListAsync();

    public Task<List<UserExchangeCredential>> GetAllByUserAsync(string userId) =>
        _context.UserExchangeCredentials
            .Include(c => c.Exchange)
            .Where(c => c.UserId == userId)
            .ToListAsync();

    public Task<List<string>> GetDistinctUserIdsByExchangeNameAsync(string exchangeName, CancellationToken ct = default) =>
        _context.UserExchangeCredentials
            .Include(c => c.Exchange)
            .Where(c => c.IsActive && c.Exchange != null
                && c.Exchange.Name == exchangeName)
            .Select(c => c.UserId)
            .Distinct()
            .ToListAsync(ct);

    public void Add(UserExchangeCredential credential) =>
        _context.UserExchangeCredentials.Add(credential);

    public void Update(UserExchangeCredential credential) =>
        _context.UserExchangeCredentials.Update(credential);

    public void Delete(UserExchangeCredential credential) =>
        _context.UserExchangeCredentials.Remove(credential);
}
