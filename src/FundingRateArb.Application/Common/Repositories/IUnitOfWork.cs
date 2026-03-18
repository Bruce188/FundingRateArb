namespace FundingRateArb.Application.Common.Repositories;

public interface IUnitOfWork : IDisposable
{
    IExchangeRepository Exchanges { get; }
    IAssetRepository Assets { get; }
    IFundingRateRepository FundingRates { get; }
    IPositionRepository Positions { get; }
    IAlertRepository Alerts { get; }
    IBotConfigRepository BotConfig { get; }
    Task<int> SaveAsync(CancellationToken ct = default);
}
