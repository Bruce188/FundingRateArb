using FundingRateArb.Application.Common.Exchanges;

namespace FundingRateArb.Application.Services;

public interface IConnectorLifecycleManager
{
    Task<(IExchangeConnector Long, IExchangeConnector Short, string? Error)> CreateUserConnectorsAsync(
        string userId, string longExchangeName, string shortExchangeName);

    (IExchangeConnector Long, IExchangeConnector Short) WrapForDryRun(
        IExchangeConnector longConnector, IExchangeConnector shortConnector);

    Task<int?> GetCachedMaxLeverageAsync(
        IExchangeConnector connector, string asset, CancellationToken ct);

}
