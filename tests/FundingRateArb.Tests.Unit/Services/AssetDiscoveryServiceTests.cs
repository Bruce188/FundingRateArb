using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Common.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Infrastructure.Repositories;
using FundingRateArb.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class AssetDiscoveryServiceTests
{
    private static AppDbContext CreateContext(string? dbName = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: dbName ?? Guid.NewGuid().ToString())
            .Options);

    private static AssetDiscoveryService CreateSut(
        AppDbContext context,
        IAssetRepository? assetRepository = null)
    {
        var repo = assetRepository
            ?? new AssetRepository(context, new MemoryCache(new MemoryCacheOptions()));
        return new AssetDiscoveryService(
            repo,
            context,
            NullLogger<AssetDiscoveryService>.Instance);
    }

    // ── Empty input ────────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureAssetsExistAsync_EmptyInput_ReturnsZero()
    {
        await using var context = CreateContext();
        var sut = CreateSut(context);

        var result = await sut.EnsureAssetsExistAsync(Enumerable.Empty<string>());

        result.Should().Be(0);
        (await context.Assets.CountAsync()).Should().Be(0);
    }

    // ── All existing — no DB write ─────────────────────────────────────────

    [Fact]
    public async Task EnsureAssetsExistAsync_AllExisting_ReturnsZeroAndSkipsDbWrite()
    {
        await using var context = CreateContext();

        // Seed BTC and ETH as active assets
        context.Assets.AddRange(
            new Asset { Symbol = "BTC", Name = "Bitcoin", IsActive = true },
            new Asset { Symbol = "ETH", Name = "Ethereum", IsActive = true });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        // Mock the repo so we can verify InvalidateCache is NOT called
        var mockRepo = new Mock<IAssetRepository>();
        mockRepo.Setup(r => r.GetActiveAsync())
            .ReturnsAsync([
                new Asset { Id = 1, Symbol = "BTC", Name = "Bitcoin", IsActive = true },
                new Asset { Id = 2, Symbol = "ETH", Name = "Ethereum", IsActive = true }
            ]);

        var sut = CreateSut(context, mockRepo.Object);

        var result = await sut.EnsureAssetsExistAsync(["BTC", "ETH"]);

        result.Should().Be(0);
        // Cache invalidation must NOT happen when nothing was inserted
        mockRepo.Verify(r => r.InvalidateCache(), Times.Never);
        // No new rows
        (await context.Assets.CountAsync()).Should().Be(2);
    }

    // ── New symbol creates asset with symbol as name ────────────────────────

    [Fact]
    public async Task EnsureAssetsExistAsync_NewSymbol_CreatesAssetWithSymbolAsName()
    {
        await using var context = CreateContext();

        var mockRepo = new Mock<IAssetRepository>();
        mockRepo.Setup(r => r.GetActiveAsync())
            .ReturnsAsync([]); // no existing assets

        var sut = CreateSut(context, mockRepo.Object);

        var result = await sut.EnsureAssetsExistAsync(["YZY"]);

        result.Should().Be(1);
        var asset = await context.Assets.SingleAsync(a => a.Symbol == "YZY");
        asset.Symbol.Should().Be("YZY");
        asset.Name.Should().Be("YZY");
        asset.IsActive.Should().BeTrue();
    }

    // ── Mixed known and unknown — only unknown inserted ─────────────────────

    [Fact]
    public async Task EnsureAssetsExistAsync_MixedKnownAndUnknown_OnlyInsertsUnknown()
    {
        await using var context = CreateContext();

        // Pre-seed BTC
        context.Assets.Add(new Asset { Symbol = "BTC", Name = "Bitcoin", IsActive = true });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var mockRepo = new Mock<IAssetRepository>();
        mockRepo.Setup(r => r.GetActiveAsync())
            .ReturnsAsync([new Asset { Id = 1, Symbol = "BTC", Name = "Bitcoin", IsActive = true }]);

        var sut = CreateSut(context, mockRepo.Object);

        var result = await sut.EnsureAssetsExistAsync(["BTC", "YZY"]);

        result.Should().Be(1);
        var assets = await context.Assets.ToListAsync();
        assets.Should().HaveCount(2);
        assets.Should().Contain(a => a.Symbol == "YZY");
    }

    // ── Duplicate inputs (case-insensitive) — only one row inserted ─────────

    [Fact]
    public async Task EnsureAssetsExistAsync_DuplicateInputsCaseInsensitive_Deduplicated()
    {
        await using var context = CreateContext();

        var mockRepo = new Mock<IAssetRepository>();
        mockRepo.Setup(r => r.GetActiveAsync()).ReturnsAsync([]);

        var sut = CreateSut(context, mockRepo.Object);

        var result = await sut.EnsureAssetsExistAsync(["yzy", "YZY", "Yzy"]);

        result.Should().Be(1);
        (await context.Assets.CountAsync()).Should().Be(1);
    }

    // ── Cache invalidated after new assets inserted ─────────────────────────

    [Fact]
    public async Task EnsureAssetsExistAsync_NewAssets_InvalidatesCache()
    {
        await using var context = CreateContext();

        var mockRepo = new Mock<IAssetRepository>();
        mockRepo.Setup(r => r.GetActiveAsync()).ReturnsAsync([]);

        var sut = CreateSut(context, mockRepo.Object);

        await sut.EnsureAssetsExistAsync(["YZY"]);

        mockRepo.Verify(r => r.InvalidateCache(), Times.Once);
    }

    // ── User preferences reconciled after new assets inserted ───────────────

    [Fact]
    public async Task EnsureAssetsExistAsync_NewAssets_ReconcilesUserPreferences()
    {
        await using var context = CreateContext();

        // Seed a user and an existing asset with an existing pref row
        context.Users.Add(new ApplicationUser { Id = "u1", UserName = "user1" });
        var existingAsset = new Asset { Symbol = "BTC", Name = "Bitcoin", IsActive = true };
        context.Assets.Add(existingAsset);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var existingPref = new UserAssetPreference
        {
            UserId = "u1",
            AssetId = existingAsset.Id,
            IsEnabled = true
        };
        context.UserAssetPreferences.Add(existingPref);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        // Mock repo returns the existing asset; YZY is the new one
        var mockRepo = new Mock<IAssetRepository>();
        mockRepo.Setup(r => r.GetActiveAsync())
            .ReturnsAsync([new Asset { Id = existingAsset.Id, Symbol = "BTC", Name = "Bitcoin", IsActive = true }]);

        var sut = CreateSut(context, mockRepo.Object);

        await sut.EnsureAssetsExistAsync(["YZY"]);

        // The new YZY asset should now have a pref row for user u1 with IsEnabled=true
        var yzyAsset = await context.Assets.SingleAsync(a => a.Symbol == "YZY");
        var yzyPref = await context.UserAssetPreferences
            .FirstOrDefaultAsync(p => p.UserId == "u1" && p.AssetId == yzyAsset.Id);

        yzyPref.Should().NotBeNull();
        yzyPref!.IsEnabled.Should().BeTrue();
    }

    // ── B1: symbols exceeding MaxLength are dropped with a warning ──────────

    [Fact]
    public async Task EnsureAssetsExistAsync_SymbolExceedsMaxLength_DropsSymbolAndWarns()
    {
        await using var context = CreateContext();

        var oversized = new string('X', 21);
        var mockRepo = new Mock<IAssetRepository>();
        mockRepo.Setup(r => r.GetActiveAsync()).ReturnsAsync([]);

        var mockLogger = new Mock<ILogger<AssetDiscoveryService>>();
        var sut = new AssetDiscoveryService(mockRepo.Object, context, mockLogger.Object);

        var result = await sut.EnsureAssetsExistAsync(new[] { "BTC", oversized });

        result.Should().Be(1);
        var assets = await context.Assets.ToListAsync();
        assets.Should().ContainSingle().Which.Symbol.Should().Be("BTC");

        // Warning logged once with the dropped count
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("dropped 1 invalid")),
                It.IsAny<Exception?>(),
                (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()),
            Times.Once);
    }

    // ── NB7: null / empty / whitespace handling ────────────────────────────

    [Fact]
    public async Task EnsureAssetsExistAsync_FiltersNullEmptyAndWhitespace()
    {
        await using var context = CreateContext();
        var mockRepo = new Mock<IAssetRepository>();
        mockRepo.Setup(r => r.GetActiveAsync()).ReturnsAsync([]);
        var sut = CreateSut(context, mockRepo.Object);

        var result = await sut.EnsureAssetsExistAsync(new[] { null!, "", "   ", "BTC" });

        result.Should().Be(1);
        (await context.Assets.SingleAsync()).Symbol.Should().Be("BTC");
    }

    [Fact]
    public async Task EnsureAssetsExistAsync_TrimsWhitespaceFromSymbols()
    {
        await using var context = CreateContext();
        var mockRepo = new Mock<IAssetRepository>();
        mockRepo.Setup(r => r.GetActiveAsync()).ReturnsAsync([]);
        var sut = CreateSut(context, mockRepo.Object);

        await sut.EnsureAssetsExistAsync(new[] { "  YZY  " });

        var asset = await context.Assets.SingleAsync();
        asset.Symbol.Should().Be("YZY");
        asset.Name.Should().Be("YZY");
    }

    // ── N3: first-seen casing preserved when input has multiple casings ─────

    [Fact]
    public async Task EnsureAssetsExistAsync_DuplicateCasings_PreservesFirstSeenCasing()
    {
        await using var context = CreateContext();
        var mockRepo = new Mock<IAssetRepository>();
        mockRepo.Setup(r => r.GetActiveAsync()).ReturnsAsync([]);
        var sut = CreateSut(context, mockRepo.Object);

        await sut.EnsureAssetsExistAsync(new[] { "yZy", "YzY" });

        var assets = await context.Assets.ToListAsync();
        assets.Should().ContainSingle();
        assets.Single().Symbol.Should().Be("yZy");
    }

    // ── NB6: SaveChanges failure path must not touch cache / reconciler ─────

    [Fact]
    public async Task EnsureAssetsExistAsync_SaveChangesFails_DoesNotInvalidateCacheOrReconcile()
    {
        await using var context = new ThrowingSaveContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        var mockRepo = new Mock<IAssetRepository>();
        mockRepo.Setup(r => r.GetActiveAsync()).ReturnsAsync([]);
        var sut = new AssetDiscoveryService(
            mockRepo.Object, context, NullLogger<AssetDiscoveryService>.Instance);

        // Non-unique-constraint DbUpdateException must propagate (it is NOT a
        // concurrent-writer race — could be a genuine schema mismatch).
        var act = () => sut.EnsureAssetsExistAsync(new[] { "YZY" });
        await act.Should().ThrowAsync<DbUpdateException>();

        mockRepo.Verify(r => r.InvalidateCache(), Times.Never);
        (await context.UserAssetPreferences.CountAsync()).Should().Be(0);
    }

    private sealed class ThrowingSaveContext(DbContextOptions<AppDbContext> options)
        : AppDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => throw new DbUpdateException(
                "simulated non-race save failure",
                new InvalidOperationException("schema mismatch"));
    }

    // ── NB2: batch exceeding MaxNewAssetsPerCycle truncates with a warning ──

    [Fact]
    public async Task EnsureAssetsExistAsync_BatchExceedsCap_TruncatesAndWarns()
    {
        await using var context = CreateContext();
        var mockRepo = new Mock<IAssetRepository>();
        mockRepo.Setup(r => r.GetActiveAsync()).ReturnsAsync([]);
        var mockLogger = new Mock<ILogger<AssetDiscoveryService>>();
        var sut = new AssetDiscoveryService(mockRepo.Object, context, mockLogger.Object);

        // 1001 unique new symbols — each fits MaxLength(20) (fits in 7 chars).
        var input = Enumerable.Range(0, 1001).Select(i => $"SYM{i:D4}").ToArray();

        var result = await sut.EnsureAssetsExistAsync(input);

        result.Should().Be(AssetDiscoveryService.MaxNewAssetsPerCycle);
        (await context.Assets.CountAsync())
            .Should().Be(AssetDiscoveryService.MaxNewAssetsPerCycle);

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("truncated batch")),
                It.IsAny<Exception?>(),
                (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()),
            Times.Once);
    }

    // ── NB1: unique-constraint race is treated as idempotent ────────────────

    [Fact]
    public async Task EnsureAssetsExistAsync_UniqueConstraintRace_TreatedAsIdempotent()
    {
        await using var context = new RaceSimulatingContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        var mockRepo = new Mock<IAssetRepository>();
        mockRepo
            .SetupSequence(r => r.GetActiveAsync())
            .ReturnsAsync([])                                                        // first call — nothing known
            .ReturnsAsync([new Asset { Id = 1, Symbol = "YZY", IsActive = true }]); // after race — other writer won

        var sut = new AssetDiscoveryService(
            mockRepo.Object, context, NullLogger<AssetDiscoveryService>.Instance);

        // Simulate: first SaveChangesAsync throws a unique-constraint exception.
        // Second save (if any) succeeds.
        context.ThrowOnFirstSave = true;

        var result = await sut.EnsureAssetsExistAsync(new[] { "YZY" });

        result.Should().Be(0, "the concurrent writer already inserted YZY");
        // Cache was invalidated as part of the retry path so callers re-read
        // the freshly-written state.
        mockRepo.Verify(r => r.InvalidateCache(), Times.AtLeastOnce);
    }

    private sealed class RaceSimulatingContext(DbContextOptions<AppDbContext> options)
        : AppDbContext(options)
    {
        public bool ThrowOnFirstSave { get; set; }
        private int _saveCount;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            _saveCount++;
            if (ThrowOnFirstSave && _saveCount == 1)
            {
                // Simulate a SqliteException with SqliteErrorCode=19 (UNIQUE).
                throw new DbUpdateException(
                    "race",
                    new UniqueConstraintInnerException("UNIQUE constraint failed: Assets.Symbol"));
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }

    // A minimal stand-in that the DbUpdateException matcher treats as a unique
    // violation via its message (the reflective SqlException/SqliteException
    // detection in the service falls through to message-substring matching).
    private sealed class UniqueConstraintInnerException(string message) : Exception(message);

    // ── NB3: reconciler is scoped to newly-inserted asset IDs ───────────────

    [Fact]
    public async Task EnsureAssetsExistAsync_NewAssets_ReconcilerScopedToNewAssetIds()
    {
        await using var context = CreateContext();

        // Seed 2 users + 10 pre-existing active assets + their full pref rows.
        context.Users.AddRange(
            new ApplicationUser { Id = "u1", UserName = "user1" },
            new ApplicationUser { Id = "u2", UserName = "user2" });
        var existingAssets = Enumerable.Range(0, 10)
            .Select(i => new Asset { Symbol = $"AST{i}", Name = $"AST{i}", IsActive = true })
            .ToList();
        context.Assets.AddRange(existingAssets);
        await context.SaveChangesAsync();

        foreach (var user in new[] { "u1", "u2" })
        {
            foreach (var a in existingAssets)
            {
                context.UserAssetPreferences.Add(new UserAssetPreference
                {
                    UserId = user,
                    AssetId = a.Id,
                    IsEnabled = true,
                });
            }
        }
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var beforeCount = await context.UserAssetPreferences.CountAsync();
        beforeCount.Should().Be(20, "2 users × 10 assets");

        var mockRepo = new Mock<IAssetRepository>();
        mockRepo.Setup(r => r.GetActiveAsync())
            .ReturnsAsync(existingAssets.Select(a => new Asset
            {
                Id = a.Id,
                Symbol = a.Symbol,
                Name = a.Name,
                IsActive = true,
            }).ToList());
        var sut = CreateSut(context, mockRepo.Object);

        await sut.EnsureAssetsExistAsync(new[] { "YZY" });

        var afterCount = await context.UserAssetPreferences.CountAsync();
        // Exactly +2 rows (2 users × 1 new asset), NOT +22 (2 × 11).
        afterCount.Should().Be(beforeCount + 2);
    }

    // ── DB union is preserved regardless of per-exchange support ──────────────

    [Fact]
    public async Task EnsureAssetsExistAsync_PersistsUnionRegardlessOfExchangeSupport()
    {
        await using var context = CreateContext();

        var mockRepo = new Mock<IAssetRepository>();
        mockRepo.Setup(r => r.GetActiveAsync()).ReturnsAsync([]);

        var sut = CreateSut(context, mockRepo.Object);

        // Both BTC and NVDA should be persisted to DB, even though NVDA is not
        // supported by all exchanges. Per-exchange filtering happens downstream
        // in MarketDataStreamManager, not in AssetDiscoveryService.
        var result = await sut.EnsureAssetsExistAsync(["BTC", "NVDA"]);

        result.Should().Be(2);
        var assets = await context.Assets.ToListAsync();
        assets.Should().HaveCount(2);
        assets.Select(a => a.Symbol).Should().BeEquivalentTo(["BTC", "NVDA"]);
    }

    // ── Pre-cancelled token throws and never touches the cache ──────────────

    [Fact]
    public async Task EnsureAssetsExistAsync_CancelledToken_Throws()
    {
        await using var context = CreateContext();
        var mockRepo = new Mock<IAssetRepository>();
        var sut = CreateSut(context, mockRepo.Object);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => sut.EnsureAssetsExistAsync(new[] { "YZY" }, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        mockRepo.Verify(r => r.InvalidateCache(), Times.Never);
    }
}
