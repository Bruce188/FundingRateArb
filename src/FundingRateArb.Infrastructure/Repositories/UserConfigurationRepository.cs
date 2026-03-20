using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FundingRateArb.Infrastructure.Repositories;

public class UserConfigurationRepository : IUserConfigurationRepository
{
    private readonly AppDbContext _context;

    public UserConfigurationRepository(AppDbContext context) => _context = context;

    public Task<UserConfiguration?> GetByUserAsync(string userId) =>
        _context.UserConfigurations
            .FirstOrDefaultAsync(c => c.UserId == userId);

    public void Add(UserConfiguration config) =>
        _context.UserConfigurations.Add(config);

    public void Update(UserConfiguration config) =>
        _context.UserConfigurations.Update(config);
}
