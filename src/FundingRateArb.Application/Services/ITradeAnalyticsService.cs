using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Application.Services;

public interface ITradeAnalyticsService
{
    Task<PositionAnalyticsDto?> GetPositionAnalyticsAsync(int positionId, string? userId = null, CancellationToken ct = default);
    Task<List<PositionAnalyticsSummaryDto>> GetAllPositionAnalyticsAsync(string? userId, int skip, int take, CancellationToken ct = default);
}
