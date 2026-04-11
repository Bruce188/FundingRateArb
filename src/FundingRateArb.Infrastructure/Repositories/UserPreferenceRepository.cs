using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FundingRateArb.Infrastructure.Repositories;

public class UserPreferenceRepository : IUserPreferenceRepository
{
    private readonly AppDbContext _context;

    public UserPreferenceRepository(AppDbContext context) => _context = context;

    public Task<List<UserExchangePreference>> GetExchangePreferencesAsync(string userId) =>
        _context.UserExchangePreferences
            .Include(p => p.Exchange)
            .Where(p => p.UserId == userId)
            .ToListAsync();

    public Task<List<UserAssetPreference>> GetAssetPreferencesAsync(string userId) =>
        _context.UserAssetPreferences
            .Include(p => p.Asset)
            .Where(p => p.UserId == userId)
            .ToListAsync();

    public Task<List<int>> GetEnabledExchangeIdsAsync(string userId) =>
        _context.UserExchangePreferences
            .Where(p => p.UserId == userId && p.IsEnabled)
            .Select(p => p.ExchangeId)
            .ToListAsync();

    public Task<List<int>> GetEnabledAssetIdsAsync(string userId) =>
        _context.UserAssetPreferences
            .Where(p => p.UserId == userId && p.IsEnabled)
            .Select(p => p.AssetId)
            .ToListAsync();

    public async Task SetExchangePreferenceAsync(string userId, int exchangeId, bool isEnabled)
    {
        var pref = await _context.UserExchangePreferences
            .FirstOrDefaultAsync(p => p.UserId == userId && p.ExchangeId == exchangeId);

        if (pref is not null)
        {
            pref.IsEnabled = isEnabled;
        }
        else
        {
            _context.UserExchangePreferences.Add(new UserExchangePreference
            {
                UserId = userId,
                ExchangeId = exchangeId,
                IsEnabled = isEnabled
            });
        }
    }

    public async Task SetAssetPreferenceAsync(string userId, int assetId, bool isEnabled)
    {
        var pref = await _context.UserAssetPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId && p.AssetId == assetId);

        if (pref is not null)
        {
            pref.IsEnabled = isEnabled;
        }
        else
        {
            _context.UserAssetPreferences.Add(new UserAssetPreference
            {
                UserId = userId,
                AssetId = assetId,
                IsEnabled = isEnabled
            });
        }
    }

    public async Task InitializeDefaultsAsync(string userId)
    {
        // Insert only the missing (user × active-exchange/asset) pairs so a re-run after
        // new exchanges/assets land in the seed backfills the user's prefs idempotently.
        var existingExchangeIds = await _context.UserExchangePreferences
            .Where(p => p.UserId == userId)
            .Select(p => p.ExchangeId)
            .ToListAsync();
        var activeExchangeIds = await _context.Exchanges
            .Where(e => e.IsActive)
            .Select(e => e.Id)
            .ToListAsync();

        foreach (var exchangeId in activeExchangeIds.Except(existingExchangeIds))
        {
            _context.UserExchangePreferences.Add(new UserExchangePreference
            {
                UserId = userId,
                ExchangeId = exchangeId,
                IsEnabled = true
            });
        }

        var existingAssetIds = await _context.UserAssetPreferences
            .Where(p => p.UserId == userId)
            .Select(p => p.AssetId)
            .ToListAsync();
        var activeAssetIds = await _context.Assets
            .Where(a => a.IsActive)
            .Select(a => a.Id)
            .ToListAsync();

        foreach (var assetId in activeAssetIds.Except(existingAssetIds))
        {
            _context.UserAssetPreferences.Add(new UserAssetPreference
            {
                UserId = userId,
                AssetId = assetId,
                IsEnabled = true
            });
        }
    }
}
