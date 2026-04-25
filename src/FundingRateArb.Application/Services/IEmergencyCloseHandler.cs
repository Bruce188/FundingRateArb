using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Services;

public interface IEmergencyCloseHandler
{
    Task<bool> TryEmergencyCloseWithRetryAsync(
        IExchangeConnector connector, string asset, Side side, string userId, CancellationToken ct);

    void SetEmergencyCloseFees(ArbitragePosition position, OrderResultDto successfulLeg, string exchangeName);
}
