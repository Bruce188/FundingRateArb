using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FundingRateArb.Infrastructure.Repositories;

public class BotConfigRepository : IBotConfigRepository
{
    private readonly AppDbContext _context;

    public BotConfigRepository(AppDbContext context) => _context = context;

    public Task<BotConfiguration> GetActiveAsync() =>
        _context.BotConfigurations.FirstAsync();

    public void Update(BotConfiguration config) =>
        _context.BotConfigurations.Update(config);
}
