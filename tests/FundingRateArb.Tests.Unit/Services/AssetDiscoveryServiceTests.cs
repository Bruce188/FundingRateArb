using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Common.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Infrastructure.Repositories;
using FundingRateArb.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
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
}
