using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FundingRateArb.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly AppDbContext _context;

    public UnitOfWork(AppDbContext context, IMemoryCache cache)
    {
        _context = context;
        Exchanges = new ExchangeRepository(context, cache);
        Assets = new AssetRepository(context, cache);
        FundingRates = new FundingRateRepository(context, cache);
        Positions = new PositionRepository(context);
        Alerts = new AlertRepository(context);
        BotConfig = new BotConfigRepository(context, cache);
        ExchangeAssetConfigs = new ExchangeAssetConfigRepository(context);
        UserCredentials = new UserExchangeCredentialRepository(context);
        UserConfigurations = new UserConfigurationRepository(context);
        UserPreferences = new UserPreferenceRepository(context);
        OpportunitySnapshots = new OpportunitySnapshotRepository(context);
    }

    public IExchangeRepository Exchanges { get; }
    public IAssetRepository Assets { get; }
    public IFundingRateRepository FundingRates { get; }
    public IPositionRepository Positions { get; }
    public IAlertRepository Alerts { get; }
    public IBotConfigRepository BotConfig { get; }
    public IExchangeAssetConfigRepository ExchangeAssetConfigs { get; }
    public IUserExchangeCredentialRepository UserCredentials { get; }
    public IUserConfigurationRepository UserConfigurations { get; }
    public IUserPreferenceRepository UserPreferences { get; }
    public IOpportunitySnapshotRepository OpportunitySnapshots { get; }

    /// <summary>Maximum retry attempts on <see cref="DbUpdateConcurrencyException"/> for ArbitragePosition writes.</summary>
    private const int PositionConcurrencyRetryAttempts = 3;

    public async Task<int> SaveAsync(CancellationToken ct = default)
    {
        // Fast path: no ArbitragePosition modifications → standard SaveAsync.
        if (!HasArbitragePositionModifications())
        {
            return await _context.SaveChangesAsync(ct);
        }

        for (var attempt = 1; attempt <= PositionConcurrencyRetryAttempts - 1; attempt++)
        {
            try
            {
                return await _context.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // Reload each conflicting entry's database values, copy in-memory CurrentValues
                // onto the reloaded snapshot (client-wins for non-conflicting columns), and reset
                // OriginalValues to the freshest RowVersion so the next SaveChangesAsync sees a
                // matching token.
                foreach (var entry in ex.Entries)
                {
                    if (entry.Entity is not ArbitragePosition)
                    {
                        // Concurrency conflicts on non-ArbitragePosition entries are not retried
                        // here; rethrow to surface them to the caller as before.
                        throw;
                    }

                    var dbValues = await entry.GetDatabaseValuesAsync(ct);
                    if (dbValues == null)
                    {
                        // Row was deleted by another writer — cannot merge; rethrow.
                        throw;
                    }

                    // Copy current in-memory values onto the reloaded snapshot, EXCLUDING RowVersion.
                    var current = entry.CurrentValues.Clone();
                    entry.OriginalValues.SetValues(dbValues);
                    foreach (var prop in entry.Metadata.GetProperties())
                    {
                        if (prop.Name == nameof(ArbitragePosition.RowVersion))
                        {
                            continue;
                        }
                        entry.CurrentValues[prop.Name] = current[prop.Name];
                    }
                }
            }
        }

        // Final attempt: if it throws, escalate via Critical alert.
        try
        {
            return await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            var positionIds = string.Join(",",
                ex.Entries
                    .Select(e => e.Entity)
                    .OfType<ArbitragePosition>()
                    .Select(p => p.Id));
            _context.Set<Alert>().Add(new Alert
            {
                Type = AlertType.OperationalWarning,
                Severity = AlertSeverity.Critical,
                Message = $"Position concurrency retry exhausted after {PositionConcurrencyRetryAttempts} attempts for position(s) {positionIds}. Write abandoned — manual reconciliation required.",
            });
            // Best-effort alert persist on a sibling SaveChangesAsync. If this also throws,
            // we still rethrow the original concurrency exception below.
            try
            {
                await _context.SaveChangesAsync(ct);
            }
            catch
            {
                // swallow — primary failure is the concurrency exhaustion
            }
            throw;
        }
    }

    private bool HasArbitragePositionModifications()
    {
        foreach (var entry in _context.ChangeTracker.Entries<ArbitragePosition>())
        {
            if (entry.State is EntityState.Modified or EntityState.Added or EntityState.Deleted)
            {
                return true;
            }
        }
        return false;
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}
