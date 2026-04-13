namespace FundingRateArb.Application.Common.Services;

public interface IAssetDiscoveryService
{
    /// <summary>
    /// Ensures Asset rows exist in the database for every symbol in the input.
    /// For any newly-created assets, reconciles UserAssetPreferences so every
    /// existing user has an IsEnabled=true row. Invalidates the asset cache
    /// on insertion. Returns the count of newly-created assets.
    /// </summary>
    Task<int> EnsureAssetsExistAsync(IEnumerable<string> symbols, CancellationToken ct = default);
}
