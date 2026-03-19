using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FundingRateArb.Infrastructure.Repositories;

public class BotConfigRepository : IBotConfigRepository
{
    private readonly AppDbContext _context;

    public BotConfigRepository(AppDbContext context) => _context = context;

    public async Task<BotConfiguration> GetActiveAsync()
    {
        var config = await _context.BotConfigurations.AsNoTracking().FirstOrDefaultAsync();
        if (config is null)
            throw new InvalidOperationException(
                "No BotConfiguration found. Run the seeder or create one via Admin UI.");
        return config;
    }

    public void Update(BotConfiguration config) =>
        _context.BotConfigurations.Update(config);
}
