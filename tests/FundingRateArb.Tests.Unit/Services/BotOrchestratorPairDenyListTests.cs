using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.BackgroundServices;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Infrastructure.Repositories;
using FundingRateArb.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

/// <summary>
/// Tests for <c>BotOrchestrator.RefreshPairDenyListAsync</c> — covers AC#7(a), (c), (e).
/// Each test builds a fresh in-memory AppDbContext + real UnitOfWork; calls the internal method directly.
/// </summary>
public class BotOrchestratorPairDenyListTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly IUnitOfWork _uow;
    private readonly PairDenyListProvider _denyListProvider;
    private readonly BotOrchestrator _sut;
    private readonly string _dbName;

    // Seeded exchange IDs
    private Exchange _hyperliquid = null!;
    private Exchange _aster = null!;

    public BotOrchestratorPairDenyListTests()
    {
        _dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(_dbName)
            .Options;
        _context = new AppDbContext(options);
        var cache = new MemoryCache(new MemoryCacheOptions());
        _uow = new UnitOfWork(_context, cache);

        // Build a minimal DI container for PairDenyListProvider (singleton needs IServiceScopeFactory)
        var services = new ServiceCollection();
        services.AddSingleton<IMemoryCache>(_ => cache);
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_dbName));
        services.AddScoped<IUnitOfWork>(sp =>
            new UnitOfWork(sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<IMemoryCache>()));
        var rootProvider = services.BuildServiceProvider();
        _denyListProvider = new PairDenyListProvider(rootProvider.GetRequiredService<IServiceScopeFactory>());

        // Wire scope factory for the IPairDenyListProvider resolve path in RefreshPairDenyListAsync
        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        var mockScope = new Mock<IServiceScope>();
        var mockSp = new Mock<IServiceProvider>();
        mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);
        mockScope.Setup(s => s.ServiceProvider).Returns(mockSp.Object);
        mockSp.Setup(p => p.GetService(typeof(IPairDenyListProvider))).Returns(_denyListProvider);

        var circuitBreaker = new CircuitBreakerManager(NullLogger<CircuitBreakerManager>.Instance);

        _sut = new BotOrchestrator(
            mockScopeFactory.Object,
            Mock.Of<IFundingRateReadinessSignal>(),
            Mock.Of<ISignalRNotifier>(),
            circuitBreaker,
            new OpportunityFilter(circuitBreaker, NullLogger<OpportunityFilter>.Instance),
            Mock.Of<IRotationEvaluator>(),
            NullLogger<BotOrchestrator>.Instance);

        SeedExchanges();
    }

    public void Dispose() => _context.Dispose();

    private void SeedExchanges()
    {
        _hyperliquid = new Exchange { Name = "Hyperliquid", ApiBaseUrl = "h", WsBaseUrl = "h" };
        _aster = new Exchange { Name = "Aster", ApiBaseUrl = "a", WsBaseUrl = "a" };
        _context.Exchanges.AddRange(_hyperliquid, _aster);
        _context.SaveChanges();
    }

    private void SeedPosition(Exchange longEx, Exchange shortEx, decimal realizedPnl, string userId = "user1")
    {
        _context.ArbitragePositions.Add(new ArbitragePosition
        {
            UserId = userId,
            LongExchangeId = longEx.Id,
            ShortExchangeId = shortEx.Id,
            Status = PositionStatus.Closed,
            RealizedPnl = realizedPnl,
            OpenedAt = DateTime.UtcNow.AddDays(-7),
            ClosedAt = DateTime.UtcNow.AddDays(-1),
        });
    }

    private static BotConfiguration MakeConfig(bool pairAutoDenyEnabled = false) => new()
    {
        IsEnabled = true,
        OperatingState = BotOperatingState.Armed,
        PairAutoDenyEnabled = pairAutoDenyEnabled,
        UpdatedByUserId = "admin",
    };

    [Fact]
    public async Task AutoDeny_Fires_When_TenClosesZeroWins_AndFlagOn()
    {
        // Seed 10 losing closes for Hyperliquid/Aster
        for (int i = 0; i < 10; i++)
            SeedPosition(_hyperliquid, _aster, -10m);
        await _context.SaveChangesAsync();

        var config = MakeConfig(pairAutoDenyEnabled: true);
        await _sut.RefreshPairDenyListAsync(_uow, config, CancellationToken.None);

        var row = await _context.PairExecutionStats.FirstOrDefaultAsync(p =>
            p.LongExchangeName == "Hyperliquid" && p.ShortExchangeName == "Aster");
        row.Should().NotBeNull();
        row!.IsDenied.Should().BeTrue();
        row.DeniedReason.Should().Be("auto: 0-win streak");
        row.DeniedUntil.Should().BeCloseTo(DateTime.UtcNow.AddDays(7), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AutoDeny_DoesNotFire_When_NineClosesZeroWins()
    {
        // Only 9 losing closes — threshold requires >= 10
        for (int i = 0; i < 9; i++)
            SeedPosition(_hyperliquid, _aster, -10m);
        await _context.SaveChangesAsync();

        var config = MakeConfig(pairAutoDenyEnabled: true);
        await _sut.RefreshPairDenyListAsync(_uow, config, CancellationToken.None);

        var row = await _context.PairExecutionStats.FirstOrDefaultAsync(p =>
            p.LongExchangeName == "Hyperliquid" && p.ShortExchangeName == "Aster");
        row.Should().NotBeNull();
        row!.IsDenied.Should().BeFalse();
    }

    [Fact]
    public async Task AutoDenyExpiry_FlipsToFalse_WhenDeniedUntilPast()
    {
        // Pre-seed an expired auto-deny row
        _context.PairExecutionStats.Add(new PairExecutionStats
        {
            LongExchangeName = "Hyperliquid",
            ShortExchangeName = "Aster",
            WindowStart = DateTime.UtcNow.AddDays(-14),
            WindowEnd = DateTime.UtcNow,
            CloseCount = 10,
            WinCount = 0,
            IsDenied = true,
            DeniedUntil = DateTime.UtcNow.AddHours(-1),
            DeniedReason = "auto: 0-win streak",
            LastUpdatedAt = DateTime.UtcNow.AddDays(-8),
        });
        await _context.SaveChangesAsync();
        // Seed one winning position so the auto-deny rule doesn't re-fire
        SeedPosition(_hyperliquid, _aster, 50m);
        await _context.SaveChangesAsync();

        var config = MakeConfig(pairAutoDenyEnabled: true);
        await _sut.RefreshPairDenyListAsync(_uow, config, CancellationToken.None);

        var row = await _context.PairExecutionStats.AsNoTracking().FirstOrDefaultAsync(p =>
            p.LongExchangeName == "Hyperliquid" && p.ShortExchangeName == "Aster");
        row.Should().NotBeNull();
        row!.IsDenied.Should().BeFalse();
        row.DeniedUntil.Should().BeNull();
        row.DeniedReason.Should().BeNull();
    }

    [Fact]
    public async Task ManualDenyExpiry_DoesNotAutoClear_WhenReasonStartsWithManual()
    {
        // Pre-seed a manual deny with past DeniedUntil — should NOT be auto-cleared
        _context.PairExecutionStats.Add(new PairExecutionStats
        {
            LongExchangeName = "Hyperliquid",
            ShortExchangeName = "Aster",
            WindowStart = DateTime.UtcNow.AddDays(-14),
            WindowEnd = DateTime.UtcNow,
            CloseCount = 5,
            WinCount = 2,
            IsDenied = true,
            DeniedUntil = DateTime.UtcNow.AddHours(-1),
            DeniedReason = "manual: user1",
            LastUpdatedAt = DateTime.UtcNow.AddDays(-1),
        });
        await _context.SaveChangesAsync();
        SeedPosition(_hyperliquid, _aster, 10m);
        await _context.SaveChangesAsync();

        var config = MakeConfig(pairAutoDenyEnabled: true);
        await _sut.RefreshPairDenyListAsync(_uow, config, CancellationToken.None);

        var row = await _context.PairExecutionStats.AsNoTracking().FirstOrDefaultAsync(p =>
            p.LongExchangeName == "Hyperliquid" && p.ShortExchangeName == "Aster");
        row.Should().NotBeNull();
        // Manual deny persists even after DeniedUntil passes
        row!.IsDenied.Should().BeTrue();
    }

    [Fact]
    public async Task AutoDeny_DoesNotFire_When_FlagOff()
    {
        // 10 losing closes but flag is off
        for (int i = 0; i < 10; i++)
            SeedPosition(_hyperliquid, _aster, -10m);
        await _context.SaveChangesAsync();

        var config = MakeConfig(pairAutoDenyEnabled: false);
        await _sut.RefreshPairDenyListAsync(_uow, config, CancellationToken.None);

        var row = await _context.PairExecutionStats.FirstOrDefaultAsync(p =>
            p.LongExchangeName == "Hyperliquid" && p.ShortExchangeName == "Aster");
        row.Should().NotBeNull();
        row!.IsDenied.Should().BeFalse();
    }
}
