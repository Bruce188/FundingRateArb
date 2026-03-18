using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Infrastructure.Data;

namespace FundingRateArb.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly AppDbContext _context;

    public UnitOfWork(AppDbContext context)
    {
        _context = context;
        Exchanges = new ExchangeRepository(context);
        Assets = new AssetRepository(context);
        FundingRates = new FundingRateRepository(context);
        Positions = new PositionRepository(context);
        Alerts = new AlertRepository(context);
        BotConfig = new BotConfigRepository(context);
    }

    public IExchangeRepository Exchanges { get; }
    public IAssetRepository Assets { get; }
    public IFundingRateRepository FundingRates { get; }
    public IPositionRepository Positions { get; }
    public IAlertRepository Alerts { get; }
    public IBotConfigRepository BotConfig { get; }

    public Task<int> SaveAsync(CancellationToken ct = default) =>
        _context.SaveChangesAsync(ct);

    public void Dispose() => _context.Dispose();
}
