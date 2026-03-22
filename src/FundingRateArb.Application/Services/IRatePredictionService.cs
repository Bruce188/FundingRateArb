using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Application.Services;

public interface IRatePredictionService
{
    Task<List<RatePredictionDto>> GetPredictionsAsync(CancellationToken ct = default);
    Task<RatePredictionDto?> GetPredictionAsync(int assetId, int exchangeId, CancellationToken ct = default);
}
