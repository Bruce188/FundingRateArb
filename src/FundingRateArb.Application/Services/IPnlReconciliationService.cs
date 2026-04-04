using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Services;

public interface IPnlReconciliationService
{
    Task ReconcileAsync(
        ArbitragePosition position,
        IExchangeConnector longConnector,
        IExchangeConnector shortConnector,
        CancellationToken ct = default);
}
