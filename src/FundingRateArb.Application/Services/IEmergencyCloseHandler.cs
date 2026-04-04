using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Services;

public interface IEmergencyCloseHandler
{
    Task<bool> TryEmergencyCloseWithRetryAsync(
        IExchangeConnector connector, string asset, Side side, string userId, CancellationToken ct);
}
