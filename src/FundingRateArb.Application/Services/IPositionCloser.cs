using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Services;

public interface IPositionCloser
{
    Task ClosePositionAsync(string userId, ArbitragePosition position, CloseReason reason, CancellationToken ct = default);
}
