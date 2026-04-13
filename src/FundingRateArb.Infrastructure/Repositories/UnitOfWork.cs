using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Infrastructure.Data;
using Microsoft.Extensions.Caching.Memory;

namespace FundingRateArb.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly AppDbContext _context;

    public UnitOfWork(AppDbContext context, IMemoryCache cache)
    {
        _context = context;
        Exchanges = new ExchangeRepository(context, cache);
        Assets = new AssetRepository(context, cache);
        FundingRates = new FundingRateRepository(context, cache);
        Positions = new PositionRepository(context);
        Alerts = new AlertRepository(context);
        BotConfig = new BotConfigRepository(context, cache);
        ExchangeAssetConfigs = new ExchangeAssetConfigRepository(context);
        UserCredentials = new UserExchangeCredentialRepository(context);
        UserConfigurations = new UserConfigurationRepository(context);
        UserPreferences = new UserPreferenceRepository(context);
        OpportunitySnapshots = new OpportunitySnapshotRepository(context);
    }

    public IExchangeRepository Exchanges { get; }
    public IAssetRepository Assets { get; }
    public IFundingRateRepository FundingRates { get; }
    public IPositionRepository Positions { get; }
    public IAlertRepository Alerts { get; }
    public IBotConfigRepository BotConfig { get; }
    public IExchangeAssetConfigRepository ExchangeAssetConfigs { get; }
    public IUserExchangeCredentialRepository UserCredentials { get; }
    public IUserConfigurationRepository UserConfigurations { get; }
    public IUserPreferenceRepository UserPreferences { get; }
    public IOpportunitySnapshotRepository OpportunitySnapshots { get; }

    public Task<int> SaveAsync(CancellationToken ct = default) =>
        _context.SaveChangesAsync(ct);

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}
