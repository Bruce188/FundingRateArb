using FluentAssertions;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Update;
using Microsoft.Extensions.Caching.Memory;
using Moq;

namespace FundingRateArb.Tests.Unit.Repositories;

/// <summary>
/// Unit tests for <see cref="UnitOfWork.SaveAsync"/> concurrency-retry filter.
/// Uses a <see cref="StubDbContext"/> that overrides <see cref="AppDbContext.SaveChangesAsync"/>
/// to throw <see cref="DbUpdateConcurrencyException"/> on cue — no real SQL Server required.
/// </summary>
public class UnitOfWorkRetryTests
{
    // ── stub ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// AppDbContext subclass that delegates SaveChangesAsync to a queue of behaviours
    /// so tests can inject throws or successes in a specific order.
    /// </summary>
    private sealed class StubDbContext : AppDbContext
    {
        public Queue<Func<Task<int>>> SaveChangesBehaviors { get; } = new();
        public int SaveChangesCallCount { get; private set; }

        public StubDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        /// <summary>Resets the call counter to 0 so tests can assert delta calls.</summary>
        public void ResetCallCount() => SaveChangesCallCount = 0;

        public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            SaveChangesCallCount++;
            if (SaveChangesBehaviors.Count == 0)
            {
                return await base.SaveChangesAsync(ct);
            }
            var behavior = SaveChangesBehaviors.Dequeue();
            return await behavior();
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static StubDbContext CreateStubContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static IMemoryCache CreateCache() => new MemoryCache(new MemoryCacheOptions());

    /// <summary>
    /// Constructs a <see cref="DbUpdateConcurrencyException"/> that points to the tracked
    /// <paramref name="entity"/> in <paramref name="context"/> so the exception's
    /// <see cref="DbUpdateConcurrencyException.Entries"/> collection has the right entity type.
    /// Uses GetInfrastructure() which is an internal EF API — suppressed per test-only usage.
    /// </summary>
#pragma warning disable EF1001 // Internal EF Core API usage — acceptable in tests
    private static DbUpdateConcurrencyException MakeConcurrencyException(
        AppDbContext context, object entity)
    {
        var entry = context.Entry(entity);
        var updateEntry = (IUpdateEntry)entry.GetInfrastructure();
        return new DbUpdateConcurrencyException("forced for test", new[] { updateEntry });
    }
#pragma warning restore EF1001

    private static ArbitragePosition BuildPosition(int openAttemptN = 0) => new()
    {
        UserId = "test-user",
        AssetId = 1,
        LongExchangeId = 1,
        ShortExchangeId = 2,
        SizeUsdc = 500m,
        MarginUsdc = 100m,
        Leverage = 5,
        LongEntryPrice = 3000m,
        ShortEntryPrice = 3000m,
        EntrySpreadPerHour = 0.001m,
        CurrentSpreadPerHour = 0.001m,
        OpenedAt = DateTime.UtcNow,
        OpenAttemptN = openAttemptN,
        RowVersion = Array.Empty<byte>(),
    };

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_OnConcurrencyConflict_RetriesUpToThreeTimesAndSucceeds()
    {
        // Arrange: context with a tracked ArbitragePosition modification.
        using var ctx = CreateStubContext();
        var position = BuildPosition();
        ctx.ArbitragePositions.Add(position);
        // Flush to InMemory so GetDatabaseValuesAsync can reload the row.
        await ctx.SaveChangesAsync();

        // Reset counter so we only count UoW-triggered calls.
        ctx.ResetCallCount();

        // Mark it modified to activate the retry path.
        position.LongFilledQuantity = 1.5m;
        ctx.Entry(position).State = EntityState.Modified;

        var exToThrow = MakeConcurrencyException(ctx, position);

        // First call throws; second call succeeds (base).
        ctx.SaveChangesBehaviors.Enqueue(() => throw exToThrow);
        ctx.SaveChangesBehaviors.Enqueue(() => Task.FromResult(1));

        var uow = new UnitOfWork(ctx, CreateCache());

        // Act
        var result = await uow.SaveAsync();

        // Assert: returned without exception; SaveChangesAsync called exactly twice (1 throw + 1 succeed).
        result.Should().Be(1);
        ctx.SaveChangesCallCount.Should().Be(2);
    }

    [Fact]
    public async Task SaveAsync_OnRepeatedConcurrencyConflict_RaisesCriticalAlertAndRethrows()
    {
        // Arrange: context throws on all 3 attempts.
        using var ctx = CreateStubContext();
        var position = BuildPosition();
        ctx.ArbitragePositions.Add(position);
        await ctx.SaveChangesAsync();
        ctx.ResetCallCount();

        position.LongFilledQuantity = 2.5m;
        ctx.Entry(position).State = EntityState.Modified;

        // Queue 3 throws (one per SaveChangesAsync in the retry loop + final attempt).
        // The 4th call is the alert-persist SaveChangesAsync — let it succeed (or fail; either is valid).
        var ex = MakeConcurrencyException(ctx, position);
        ctx.SaveChangesBehaviors.Enqueue(() => throw ex);
        ctx.SaveChangesBehaviors.Enqueue(() => throw ex);
        ctx.SaveChangesBehaviors.Enqueue(() => throw ex);
        // 4th call = alert-persist (swallowed internally even if it throws)
        ctx.SaveChangesBehaviors.Enqueue(() => Task.FromResult(1));

        var uow = new UnitOfWork(ctx, CreateCache());

        // Act + Assert: SaveAsync should rethrow DbUpdateConcurrencyException.
        var action = async () => await uow.SaveAsync();
        await action.Should().ThrowAsync<DbUpdateConcurrencyException>();

        // Assert: a Critical alert was added to the change tracker.
        ctx.Set<Alert>().Local.Any(a =>
            a.Type == AlertType.OperationalWarning &&
            a.Severity == AlertSeverity.Critical)
            .Should().BeTrue("retry exhaustion must add a Critical OperationalWarning alert");
    }

    [Fact]
    public async Task SaveAsync_NoArbitragePositionModifications_BypassesRetryFastPath()
    {
        // Arrange: only an Alert entity is modified (no ArbitragePosition) → fast path.
        using var ctx = CreateStubContext();

        // Add an alert directly (not tracked as ArbitragePosition).
        var alert = new Alert
        {
            Type = AlertType.OperationalWarning,
            Severity = AlertSeverity.Warning,
            Message = "test",
        };
        ctx.Alerts.Add(alert);

        // Queue exactly one success — fast path must call SaveChangesAsync exactly once.
        ctx.SaveChangesBehaviors.Enqueue(() => Task.FromResult(1));

        var uow = new UnitOfWork(ctx, CreateCache());

        // Act
        var result = await uow.SaveAsync();

        // Assert: SaveChangesAsync called exactly once (fast path bypassed retry loop).
        result.Should().Be(1);
        ctx.SaveChangesCallCount.Should().Be(1);
    }

    [Fact]
    public async Task SaveAsync_OnNonArbitragePositionConcurrencyConflict_DoesNotRetry()
    {
        // Arrange: both an Asset AND an ArbitragePosition are tracked (so fast-path returns false),
        // but the exception references only the Asset entry → the retry filter rethrows immediately.
        using var ctx = CreateStubContext();

        var position = BuildPosition();
        ctx.ArbitragePositions.Add(position);
        await ctx.SaveChangesAsync();

        // Add an asset modification to the same context.
        var asset = new Asset { Id = 99, Symbol = "BTC", Name = "Bitcoin", IsActive = true };
        ctx.Assets.Add(asset);
        await ctx.SaveChangesAsync();

        // Reset counter so only UoW-triggered calls are counted.
        ctx.ResetCallCount();

        position.LongFilledQuantity = 1.5m;
        ctx.Entry(position).State = EntityState.Modified;
        ctx.Entry(asset).State = EntityState.Modified;

        // Exception references the Asset entry (non-ArbitragePosition) — must rethrow without retry.
        var assetEx = MakeConcurrencyException(ctx, asset);
        ctx.SaveChangesBehaviors.Enqueue(() => throw assetEx);

        var uow = new UnitOfWork(ctx, CreateCache());

        // Act + Assert: rethrows on first attempt (no retry).
        var action = async () => await uow.SaveAsync();
        await action.Should().ThrowAsync<DbUpdateConcurrencyException>();

        // SaveChangesAsync was called exactly once.
        ctx.SaveChangesCallCount.Should().Be(1);
    }
}
