namespace FundingRateArb.Application.Common.Repositories;

public interface IUnitOfWork : IDisposable
{
    IExchangeRepository Exchanges { get; }
    IAssetRepository Assets { get; }
    IFundingRateRepository FundingRates { get; }
    IPositionRepository Positions { get; }
    IAlertRepository Alerts { get; }
    IBotConfigRepository BotConfig { get; }
    IExchangeAssetConfigRepository ExchangeAssetConfigs { get; }
    IUserExchangeCredentialRepository UserCredentials { get; }
    IUserConfigurationRepository UserConfigurations { get; }
    IUserPreferenceRepository UserPreferences { get; }
    IOpportunitySnapshotRepository OpportunitySnapshots { get; }
    Task<int> SaveAsync(CancellationToken ct = default);
}
