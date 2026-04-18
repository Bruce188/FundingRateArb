using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.BackgroundServices;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FundingRateArb.Tests.Integration;

[Collection("IntegrationTests")]
public class BotOrchestratorBootSweepTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static AppDbContext CreateContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<(Exchange LongExchange, Exchange ShortExchange, Asset Asset, ApplicationUser User)>
        SeedReferenceDataAsync(AppDbContext db)
    {
        var user = new ApplicationUser { Id = "sweep-user", UserName = "sweep@test.com", Email = "sweep@test.com" };
        db.Users.Add(user);

        var longExchange = new Exchange { Id = 10, Name = "Lighter", ApiBaseUrl = "https://l.test", WsBaseUrl = "wss://l.test" };
        var shortExchange = new Exchange { Id = 11, Name = "Aster", ApiBaseUrl = "https://a.test", WsBaseUrl = "wss://a.test" };
        db.Exchanges.AddRange(longExchange, shortExchange);

        var asset = new Asset { Id = 5, Symbol = "ETH", Name = "Ethereum", IsActive = true };
        db.Assets.Add(asset);

        db.BotConfigurations.Add(new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            OpenConfirmTimeoutSeconds = 5,
            UpdatedByUserId = "sweep-user"
        });

        db.UserConfigurations.Add(new UserConfiguration
        {
            UserId = "sweep-user",
            IsEnabled = true
        });

        await db.SaveChangesAsync();
        return (longExchange, shortExchange, asset, user);
    }

    private static ArbitragePosition BuildOpeningPosition(
        string userId, int assetId, int longExchangeId, int shortExchangeId)
        => new()
        {
            UserId = userId,
            AssetId = assetId,
            LongExchangeId = longExchangeId,
            ShortExchangeId = shortExchangeId,
            Status = PositionStatus.Opening,
            OpenConfirmedAt = null,
            SizeUsdc = 500m,
            MarginUsdc = 100m,
            Leverage = 5,
            LongEntryPrice = 1_500m,
            ShortEntryPrice = 1_500m,
            EntrySpreadPerHour = 0.001m,
            CurrentSpreadPerHour = 0.001m,
            OpenedAt = DateTime.UtcNow.AddMinutes(-5),
        };

    /// <summary>
    /// Builds a <see cref="BotOrchestrator"/> with real <see cref="IUnitOfWork"/> (in-memory DB)
    /// and a stub <see cref="IExecutionEngine"/>. All other dependencies are no-op stubs since
    /// <see cref="BotOrchestrator.RunBootSweepAsync"/> only uses <c>IUnitOfWork</c> and
    /// <c>IExecutionEngine</c>.
    /// </summary>
    private static BotOrchestrator CreateOrchestrator(AppDbContext db, IExecutionEngine executionEngine)
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddSingleton<AppDbContext>(_ => db);
        services.AddScoped<IUnitOfWork>(sp =>
            new UnitOfWork(sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<IMemoryCache>()));
        services.AddScoped<IExecutionEngine>(_ => executionEngine);

        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new BotOrchestrator(
            scopeFactory,
            new NullReadinessSignal(),
            new NullSignalRNotifier(),
            new NullCircuitBreakerManager(),
            new NullOpportunityFilter(),
            new NullRotationEvaluator(),
            NullLogger<BotOrchestrator>.Instance);
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Boot sweep must call ConfirmOrRollbackAsync for each pending Opening position
    /// belonging to a bot-enabled user.
    /// </summary>
    [Fact]
    public async Task BootSweep_PendingOpeningPosition_CallsConfirmOrRollback()
    {
        var dbName = $"BootSweep_{Guid.NewGuid()}";
        await using var db = CreateContext(dbName);
        var (longEx, shortEx, asset, user) = await SeedReferenceDataAsync(db);

        var position = BuildOpeningPosition(user.Id, asset.Id, longEx.Id, shortEx.Id);
        db.ArbitragePositions.Add(position);
        await db.SaveChangesAsync();

        var stub = new CapturingExecutionEngineStub();
        var orchestrator = CreateOrchestrator(db, stub);

        await orchestrator.RunBootSweepAsync(CancellationToken.None);

        stub.ConfirmOrRollbackCalls.Should().HaveCount(1,
            "one pending Opening position should trigger exactly one ConfirmOrRollbackAsync call");
        stub.ConfirmOrRollbackCalls[0].UserId.Should().Be(user.Id);
        stub.ConfirmOrRollbackCalls[0].PositionId.Should().Be(position.Id);
    }

    /// <summary>
    /// Boot sweep must not call ConfirmOrRollbackAsync for positions already in Open state.
    /// </summary>
    [Fact]
    public async Task BootSweep_OpenPosition_SkipsConfirmOrRollback()
    {
        var dbName = $"BootSweepSkip_{Guid.NewGuid()}";
        await using var db = CreateContext(dbName);
        var (longEx, shortEx, asset, user) = await SeedReferenceDataAsync(db);

        var openPosition = BuildOpeningPosition(user.Id, asset.Id, longEx.Id, shortEx.Id);
        openPosition.Status = PositionStatus.Open;
        openPosition.OpenConfirmedAt = DateTime.UtcNow.AddMinutes(-1);
        db.ArbitragePositions.Add(openPosition);
        await db.SaveChangesAsync();

        var stub = new CapturingExecutionEngineStub();
        var orchestrator = CreateOrchestrator(db, stub);

        await orchestrator.RunBootSweepAsync(CancellationToken.None);

        stub.ConfirmOrRollbackCalls.Should().BeEmpty(
            "positions already in Open state must not be reprocessed by the boot sweep");
    }

    /// <summary>
    /// Boot sweep must process pending Opening positions across multiple enabled users.
    /// </summary>
    [Fact]
    public async Task BootSweep_MultipleUsersWithPendingPositions_ProcessesAll()
    {
        var dbName = $"BootSweepMulti_{Guid.NewGuid()}";
        await using var db = CreateContext(dbName);
        var (longEx, shortEx, asset, user) = await SeedReferenceDataAsync(db);

        var user2 = new ApplicationUser { Id = "sweep-user-2", UserName = "sweep2@test.com", Email = "sweep2@test.com" };
        db.Users.Add(user2);
        db.UserConfigurations.Add(new UserConfiguration { UserId = user2.Id, IsEnabled = true });

        var pos1 = BuildOpeningPosition(user.Id, asset.Id, longEx.Id, shortEx.Id);
        var pos2 = BuildOpeningPosition(user2.Id, asset.Id, longEx.Id, shortEx.Id);
        db.ArbitragePositions.AddRange(pos1, pos2);
        await db.SaveChangesAsync();

        var stub = new CapturingExecutionEngineStub();
        var orchestrator = CreateOrchestrator(db, stub);

        await orchestrator.RunBootSweepAsync(CancellationToken.None);

        stub.ConfirmOrRollbackCalls.Should().HaveCount(2,
            "boot sweep must process one pending Opening position per enabled user");
        stub.ConfirmOrRollbackCalls.Select(c => c.UserId).Should().BeEquivalentTo(
            new[] { user.Id, user2.Id },
            "both enabled users' positions must be processed");
    }

    // ── capturing stub ────────────────────────────────────────────────────────

    private sealed class CapturingExecutionEngineStub : IExecutionEngine
    {
        public List<(string UserId, int PositionId)> ConfirmOrRollbackCalls { get; } = new();

        public Task<(bool Success, string? Error)> OpenPositionAsync(
            string userId, ArbitrageOpportunityDto opp, decimal sizeUsdc,
            UserConfiguration? userConfig = null, CancellationToken ct = default)
            => Task.FromResult((false, (string?)"stub"));

        public Task ClosePositionAsync(string userId, ArbitragePosition position,
            CloseReason reason, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool?> CheckPositionExistsOnExchangesAsync(
            ArbitragePosition position, CancellationToken ct = default)
            => Task.FromResult<bool?>(null);

        public Task<Dictionary<int, PositionExistsResult>> CheckPositionsExistOnExchangesBatchAsync(
            IReadOnlyList<ArbitragePosition> positions, CancellationToken ct = default)
            => Task.FromResult(new Dictionary<int, PositionExistsResult>());

        public Task ConfirmOrRollbackAsync(string userId, ArbitragePosition position,
            int timeoutSeconds, CancellationToken ct = default)
        {
            ConfirmOrRollbackCalls.Add((userId, position.Id));
            return Task.CompletedTask;
        }
    }

    // ── no-op dependency stubs ────────────────────────────────────────────────
    // RunBootSweepAsync only uses IUnitOfWork + IExecutionEngine from the scope.
    // The remaining BotOrchestrator constructor dependencies are only used in RunCycleAsync.

    private sealed class NullReadinessSignal : IFundingRateReadinessSignal
    {
        public Task WaitForReadyAsync(CancellationToken ct) => Task.CompletedTask;
        public void SignalReady() { }
    }

    private sealed class NullSignalRNotifier : ISignalRNotifier
    {
        public Task PushOpportunityUpdateAsync(OpportunityResultDto result) => Task.CompletedTask;

        public Task PushDashboardUpdateAsync(
            List<ArbitragePosition> openPositions, List<ArbitrageOpportunityDto> opportunities,
            BotOperatingState operatingState, int openingCount, int needsAttentionCount,
            OpportunityResultDto? opportunityResult = null) => Task.CompletedTask;

        public Task PushPositionUpdatesAsync(
            List<ArbitragePosition> openPositions, BotConfiguration config,
            IReadOnlyDictionary<int, ComputedPositionPnl>? computedPnl = null) => Task.CompletedTask;

        public Task PushPositionRemovalsAsync(
            IReadOnlyList<(int PositionId, string UserId, int LongExchangeId, int ShortExchangeId, PositionStatus OriginalStatus)> reapedPositions,
            List<(int PositionId, string UserId)> closedPositions) => Task.CompletedTask;

        public Task PushRebalanceRemovalsAsync(List<(int Id, string UserId)> removals) => Task.CompletedTask;

        public Task PushNewAlertsAsync(IUnitOfWork uow) => Task.CompletedTask;

        public Task PushStatusExplanationAsync(string? userId, string message, string severity) => Task.CompletedTask;

        public Task PushBalanceUpdateAsync(string userId, BalanceSnapshotDto snapshot) => Task.CompletedTask;

        public Task PushNotificationAsync(string userId, string message) => Task.CompletedTask;
    }

    private sealed class NullCircuitBreakerManager : ICircuitBreakerManager
    {
        public TimeSpan BaseCooldownDuration => TimeSpan.FromMinutes(5);
        public TimeSpan MaxCooldownDuration => TimeSpan.FromHours(1);
        public TimeSpan RotationCooldownDuration => TimeSpan.FromMinutes(30);

        public void SweepExpiredEntries() { }
        public bool IncrementExchangeFailure(int exchangeId, BotConfiguration config, bool isAuthError = false) => false;
        public void IncrementAssetExchangeFailure(int assetId, int exchangeId) { }
        public void RecordCloseResult(decimal realizedPnl, string? userId) { }
        public IReadOnlyList<CircuitBreakerStatusDto> GetCircuitBreakerStates() => Array.Empty<CircuitBreakerStatusDto>();
        public IReadOnlyList<ActiveCooldownDto> GetActivePairCooldowns() => Array.Empty<ActiveCooldownDto>();
        public void ClearCooldowns() { }
        public HashSet<int> GetCircuitBrokenExchangeIds() => new();
        public bool IsOnCooldown(string cooldownKey, out TimeSpan remaining) { remaining = default; return false; }
        public DateTime? GetCooldownUntil(string cooldownKey) => null;
        public (DateTime CooldownUntil, int Failures) GetCooldownEntry(string cooldownKey) => (DateTime.MinValue, 0);
        public void SetCooldown(string key, DateTime cooldownUntil, int failures) { }
        public void RemoveCooldown(string key) { }
        public bool IsAssetExchangeOnCooldown(int assetId, int exchangeId) => false;
        public void RemoveAssetExchangeCooldown(int assetId, int exchangeId) { }
        public void SetExchangeCircuitBreaker(int exchangeId, int failures, DateTime brokenUntil) { }
        public void RemoveExchangeCircuitBreaker(int exchangeId) { }
        public DateTime? GetRotationCooldown(string key) => null;
        public void SetRotationCooldown(string key, DateTime until) { }
        public (DateOnly Date, int Count) GetDailyRotationCount(string userId) => (DateOnly.MinValue, 0);
        public void SetDailyRotationCount(string userId, DateOnly date, int count) { }
        public int GetConsecutiveLosses(string userId) => 0;
    }

    private sealed class NullOpportunityFilter : IOpportunityFilter
    {
        public List<ArbitrageOpportunityDto> FilterUserOpportunities(
            List<ArbitrageOpportunityDto> allOpportunities,
            HashSet<int> enabledExchangeSet,
            HashSet<int> dataOnlyExchangeIds,
            HashSet<int> circuitBrokenExchangeIds,
            HashSet<int> enabledAssetSet,
            UserConfiguration userConfig,
            SkipReasonTracker tracker)
            => new();

        public List<ArbitrageOpportunityDto> FilterCandidates(
            List<ArbitrageOpportunityDto> userOpportunities,
            HashSet<string> allActiveKeys,
            string userId,
            SkipReasonTracker tracker,
            out List<(string Asset, TimeSpan Remaining)> cooldownSkips)
        {
            cooldownSkips = new List<(string, TimeSpan)>();
            return new();
        }

        public List<ArbitrageOpportunityDto> FindAdaptiveCandidates(
            List<ArbitrageOpportunityDto> allNetPositive,
            HashSet<int> enabledExchangeSet,
            HashSet<int> dataOnlyExchangeIds,
            HashSet<int> circuitBrokenExchangeIds,
            HashSet<int> enabledAssetSet,
            HashSet<string> activeKeys,
            string userId,
            SkipReasonTracker tracker)
            => new();
    }

    private sealed class NullRotationEvaluator : IRotationEvaluator
    {
        public RotationRecommendationDto? Evaluate(
            IReadOnlyList<ArbitragePosition> openPositions,
            IReadOnlyList<ArbitrageOpportunityDto> opportunities,
            UserConfiguration userConfig,
            BotConfiguration globalConfig)
            => null;
    }
}
