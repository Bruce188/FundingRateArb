using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Services;

public interface IExecutionEngine
{
    Task<(bool Success, string? Error)> OpenPositionAsync(string userId, ArbitrageOpportunityDto opp, decimal sizeUsdc, UserConfiguration? userConfig = null, CancellationToken ct = default);
    Task ClosePositionAsync(string userId, ArbitragePosition position, CloseReason reason, CancellationToken ct = default);
}
