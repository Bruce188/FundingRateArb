using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Common.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.Services;

public class AssetDiscoveryService : IAssetDiscoveryService
{
    private readonly IAssetRepository _assetRepository;
    private readonly AppDbContext _context;
    private readonly ILogger<AssetDiscoveryService> _logger;

    public AssetDiscoveryService(
        IAssetRepository assetRepository,
        AppDbContext context,
        ILogger<AssetDiscoveryService> logger)
    {
        _assetRepository = assetRepository;
        _context = context;
        _logger = logger;
    }

    public async Task<int> EnsureAssetsExistAsync(
        IEnumerable<string> symbols, CancellationToken ct = default)
    {
        // 1. Normalise: distinct, case-insensitive, non-empty, trimmed
        var normalised = symbols
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalised.Count == 0)
        {
            return 0;
        }

        // 2. Load current active assets (cached, fast path)
        var existing = await _assetRepository.GetActiveAsync();

        // 3. Build a case-insensitive set of known symbols
        var existingSymbols = new HashSet<string>(
            existing.Select(a => a.Symbol),
            StringComparer.OrdinalIgnoreCase);

        // 4. Compute diff
        var missing = normalised.Where(s => !existingSymbols.Contains(s)).ToList();

        // 5. Nothing new — return silently (hot-path, no DB writes, no log spam)
        if (missing.Count == 0)
        {
            return 0;
        }

        // 6. Insert new Asset rows
        foreach (var symbol in missing)
        {
            _context.Assets.Add(new Asset
            {
                Symbol = symbol,
                Name = symbol,
                IsActive = true
            });
        }

        // 7. Persist
        await _context.SaveChangesAsync(ct);

        // 8. Reconcile user preferences for the new assets
        await UserPreferenceReconciler.ReconcileUserAssetPreferencesAsync(_context, ct);

        // 9. Invalidate the asset cache so the next GetActiveAsync returns the new rows
        _assetRepository.InvalidateCache();

        // 10. Log discovery
        _logger.LogInformation(
            "Auto-discovered {Count} new assets: {Symbols}",
            missing.Count,
            string.Join(", ", missing));

        return missing.Count;
    }
}
