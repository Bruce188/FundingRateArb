using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Services;

public interface IExecutionEngine
{
    Task<(bool Success, string? Error)> OpenPositionAsync(ArbitrageOpportunityDto opp, decimal sizeUsdc, CancellationToken ct = default);
    Task ClosePositionAsync(ArbitragePosition position, CloseReason reason, CancellationToken ct = default);
}
