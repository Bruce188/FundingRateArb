using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Application.Services;

public interface IBalanceAggregator
{
    Task<BalanceSnapshotDto> GetBalanceSnapshotAsync(string userId, CancellationToken ct = default);
}
