using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Services;

public interface IPositionHealthMonitor
{
    Task<IReadOnlyList<(ArbitragePosition Position, CloseReason Reason)>> CheckAndActAsync(CancellationToken ct = default);
}
