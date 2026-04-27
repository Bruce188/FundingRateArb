using System.Reflection;
using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Domain.ValueObjects;
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

        /// <summary>The last <see cref="ArbitragePosition"/> passed to ConfirmOrRollbackAsync.</summary>
        public ArbitragePosition? LastReceivedPosition { get; private set; }

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
            LastReceivedPosition = position;
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
        public void MarkUnavailable(string exchange) { }
        public bool IsUnavailable(string exchange) => false;
        public void ClearUnavailable(string exchange) { }
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

    // ── Task 6.3: boot-replay test ────────────────────────────────────────────

    /// <summary>
    /// A position already in Opening state with non-zero attempt counters must go through
    /// <see cref="IExecutionEngine.ConfirmOrRollbackAsync"/>, which polls
    /// <see cref="IExchangeConnector.HasOpenPositionAsync"/> on both legs. It must NOT call
    /// <see cref="IExchangeConnector.PlaceMarketOrderByQuantityAsync"/> — the boot sweep
    /// confirms existing fills; it never resubmits orders.
    /// </summary>
    [Fact]
    public async Task BootSweep_OpeningPositionWithAttemptCounters_CallsHasOpenPosition_NeverCallsPlaceOrder()
    {
        var dbName = $"BootSweepIdempotency_{Guid.NewGuid()}";
        await using var db = CreateContext(dbName);
        var (longEx, shortEx, asset, user) = await SeedReferenceDataAsync(db);

        // Seed a position stuck in Opening with existing attempt counters (simulating a restart
        // mid-confirmation window after clientOrderIds were already submitted to the exchange).
        var position = BuildOpeningPosition(user.Id, asset.Id, longEx.Id, shortEx.Id);
        position.LongOrderAttemptN = 1;
        position.ShortOrderAttemptN = 1;
        db.ArbitragePositions.Add(position);
        await db.SaveChangesAsync();

        // Stub connectors that track HasOpenPositionAsync and PlaceMarketOrderByQuantityAsync calls.
        var longConnector = new TrackingConnectorStub("Lighter", opensPosition: true);
        var shortConnector = new TrackingConnectorStub("Aster", opensPosition: true);

        var lifecycleManager = new PreconfiguredConnectorLifecycleManager(longConnector, shortConnector);

        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddSingleton<AppDbContext>(_ => db);
        services.AddScoped<IUnitOfWork>(sp =>
            new UnitOfWork(sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<IMemoryCache>()));
        services.AddScoped<IExecutionEngine>(sp =>
        {
            var uow = sp.GetRequiredService<IUnitOfWork>();
            var emergencyClose = new EmergencyCloseHandler(uow, NullLogger<EmergencyCloseHandler>.Instance);
            var positionCloser = new PositionCloser(uow, lifecycleManager,
                new NullPnlReconciliationService(), NullLogger<PositionCloser>.Instance);
            return new ExecutionEngine(
                uow, lifecycleManager, emergencyClose, positionCloser,
                new NullUserSettingsService(),
                new NullLeverageTierProvider(),
                new NullBalanceAggregator(),
                NullLogger<ExecutionEngine>.Instance);
        });

        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var orchestrator = new BotOrchestrator(
            scopeFactory,
            new NullReadinessSignal(),
            new NullSignalRNotifier(),
            new NullCircuitBreakerManager(),
            new NullOpportunityFilter(),
            new NullRotationEvaluator(),
            NullLogger<BotOrchestrator>.Instance);

        await orchestrator.RunBootSweepAsync(CancellationToken.None);

        longConnector.HasOpenPositionCallCount.Should().BeGreaterThan(0,
            "ConfirmOrRollbackAsync must poll HasOpenPositionAsync on the long-leg connector");
        shortConnector.HasOpenPositionCallCount.Should().BeGreaterThan(0,
            "ConfirmOrRollbackAsync must poll HasOpenPositionAsync on the short-leg connector");
        longConnector.PlaceOrderCallCount.Should().Be(0,
            "boot sweep must never resubmit orders — PlaceMarketOrderByQuantityAsync must not be called on the long connector");
        shortConnector.PlaceOrderCallCount.Should().Be(0,
            "boot sweep must never resubmit orders — PlaceMarketOrderByQuantityAsync must not be called on the short connector");
    }

    // ── stubs for Task 6.3 ────────────────────────────────────────────────────

    /// <summary>
    /// Connector stub that counts calls to <see cref="IExchangeConnector.HasOpenPositionAsync"/> and
    /// <see cref="IExchangeConnector.PlaceMarketOrderByQuantityAsync"/> without performing real I/O.
    /// </summary>
    private sealed class TrackingConnectorStub : IExchangeConnector
    {
        private int _hasOpenPositionCallCount;
        private int _placeOrderCallCount;

        public int HasOpenPositionCallCount => _hasOpenPositionCallCount;
        public int PlaceOrderCallCount => _placeOrderCallCount;

        private readonly bool _opensPosition;

        public TrackingConnectorStub(string exchangeName, bool opensPosition)
        {
            ExchangeName = exchangeName;
            _opensPosition = opensPosition;
        }

        public string ExchangeName { get; }
        public bool IsEstimatedFillExchange => false;

        public Task<bool?> HasOpenPositionAsync(string asset, Side side, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _hasOpenPositionCallCount);
            return Task.FromResult<bool?>(_opensPosition);
        }

        public Task<OrderResultDto> PlaceMarketOrderByQuantityAsync(
            string asset, Side side, decimal quantity, int leverage,
            string? clientOrderId = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _placeOrderCallCount);
            return Task.FromResult(new OrderResultDto { Success = false, Error = "stub — should not be called" });
        }

        // ── remaining non-default interface members ───────────────────────────
        public Task<List<FundingRateDto>> GetFundingRatesAsync(CancellationToken ct = default)
            => Task.FromResult(new List<FundingRateDto>());
        public Task<OrderResultDto> PlaceMarketOrderAsync(string asset, Side side, decimal sizeUsdc, int leverage, CancellationToken ct = default)
            => Task.FromResult(new OrderResultDto { Success = false, Error = "stub" });
        public Task<OrderResultDto> ClosePositionAsync(string asset, Side side, CancellationToken ct = default)
            => Task.FromResult(new OrderResultDto { Success = false, Error = "stub" });
        public Task<decimal> GetMarkPriceAsync(string asset, CancellationToken ct = default) => Task.FromResult(0m);
        public Task<decimal> GetAvailableBalanceAsync(CancellationToken ct = default) => Task.FromResult(0m);
        public Task<int?> GetMaxLeverageAsync(string asset, CancellationToken ct = default) => Task.FromResult<int?>(null);
        public Task<DateTime?> GetNextFundingTimeAsync(string asset, CancellationToken ct = default) => Task.FromResult<DateTime?>(null);
    }

    /// <summary>
    /// <see cref="IConnectorLifecycleManager"/> stub that returns pre-built connectors without
    /// performing credential resolution or factory calls.
    /// </summary>
    private sealed class PreconfiguredConnectorLifecycleManager : IConnectorLifecycleManager
    {
        private readonly IExchangeConnector _long;
        private readonly IExchangeConnector _short;

        public PreconfiguredConnectorLifecycleManager(IExchangeConnector longConnector, IExchangeConnector shortConnector)
        {
            _long = longConnector;
            _short = shortConnector;
        }

        public Task<(IExchangeConnector Long, IExchangeConnector Short, string? Error)> CreateUserConnectorsAsync(
            string userId, string longExchangeName, string shortExchangeName)
            => Task.FromResult<(IExchangeConnector, IExchangeConnector, string?)>((_long, _short, null));

        public (IExchangeConnector Long, IExchangeConnector Short) WrapForDryRun(
            IExchangeConnector longConnector, IExchangeConnector shortConnector)
            => (longConnector, shortConnector);

        public Task<int?> GetCachedMaxLeverageAsync(IExchangeConnector connector, string asset, CancellationToken ct)
            => Task.FromResult<int?>(null);

        public Task EnsureTiersCachedAsync(IExchangeConnector connector, string asset, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class NullPnlReconciliationService : IPnlReconciliationService
    {
        public Task ReconcileAsync(
            ArbitragePosition position,
            string assetSymbol,
            IExchangeConnector longConnector,
            IExchangeConnector shortConnector,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class NullUserSettingsService : IUserSettingsService
    {
        public Task SaveCredentialAsync(string userId, int exchangeId, string? apiKey, string? apiSecret, string? walletAddress, string? privateKey, string? subAccountAddress = null, string? apiKeyIndex = null) => Task.CompletedTask;
        public Task<UserExchangeCredential?> GetCredentialAsync(string userId, int exchangeId) => Task.FromResult<UserExchangeCredential?>(null);
        public Task<List<UserExchangeCredential>> GetActiveCredentialsAsync(string userId) => Task.FromResult(new List<UserExchangeCredential>());
        public Task<List<UserExchangeCredential>> GetAllCredentialsAsync(string userId) => Task.FromResult(new List<UserExchangeCredential>());
        public Task DeleteCredentialAsync(string userId, int exchangeId) => Task.CompletedTask;
        public (string? ApiKey, string? ApiSecret, string? WalletAddress, string? PrivateKey, string? SubAccountAddress, string? ApiKeyIndex) DecryptCredential(UserExchangeCredential credential) => (null, null, null, null, null, null);
        public Task<UserConfiguration> GetOrCreateConfigAsync(string userId) => Task.FromResult(new UserConfiguration());
        public Task UpdateConfigAsync(string userId, UserConfiguration config) => Task.CompletedTask;
        public Task<List<Exchange>> GetAvailableExchangesAsync() => Task.FromResult(new List<Exchange>());
        public Task<List<int>> GetDataOnlyExchangeIdsAsync() => Task.FromResult(new List<int>());
        public Task<List<Asset>> GetAvailableAssetsAsync() => Task.FromResult(new List<Asset>());
        public Task<List<int>> GetUserEnabledExchangeIdsAsync(string userId) => Task.FromResult(new List<int>());
        public Task<List<int>> GetUserEnabledAssetIdsAsync(string userId) => Task.FromResult(new List<int>());
        public Task SetExchangePreferenceAsync(string userId, int exchangeId, bool isEnabled) => Task.CompletedTask;
        public Task SetAssetPreferenceAsync(string userId, int assetId, bool isEnabled) => Task.CompletedTask;
        public Task SavePreferencesAsync(string userId, Dictionary<int, bool> exchangePreferences, Dictionary<int, bool> assetPreferences) => Task.CompletedTask;
        public Task InitializeDefaultsForNewUserAsync(string userId) => Task.CompletedTask;
        public Task<bool> HasValidCredentialsAsync(string userId) => Task.FromResult(false);
        public Task<List<string>> GetUsersWithCredentialsAsync(string exchange, CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task UpdateCredentialErrorAsync(string userId, int exchangeId, string? error, CancellationToken ct = default) => Task.CompletedTask;
        public Task TouchLastUsedAsync(string userId, int exchangeId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NullLeverageTierProvider : ILeverageTierProvider
    {
        public Task<LeverageTier[]> GetTiersAsync(string exchangeName, string asset, CancellationToken ct = default)
            => Task.FromResult(Array.Empty<LeverageTier>());
        public int GetEffectiveMaxLeverage(string exchangeName, string asset, decimal notionalUsdc) => int.MaxValue;
        public decimal GetMaintenanceMarginRate(string exchangeName, string asset, decimal notionalUsdc) => 0m;
        public void UpdateTiers(string exchangeName, string asset, LeverageTier[] tiers) { }
        public bool IsStale(string exchangeName, string asset) => false;
    }

    private sealed class NullBalanceAggregator : IBalanceAggregator
    {
        public Task<BalanceSnapshotDto> GetBalanceSnapshotAsync(string userId, CancellationToken ct = default)
            => Task.FromResult(new BalanceSnapshotDto { Balances = new List<ExchangeBalanceDto>(), TotalAvailableUsdc = 0m, FetchedAt = DateTime.UtcNow });
    }

    // ── Task 5.4: cycle-lock and restart-mid-rollback tests ───────────────────

    /// <summary>
    /// Seeds an Opening position with a specific <paramref name="openAttemptN"/> value
    /// so the boot sweep sees it and passes it to the CapturingExecutionEngineStub.
    /// </summary>
    private static async Task<int> SeedOpeningPositionWithAttemptCountAsync(
        AppDbContext db, int openAttemptN)
    {
        var user = await db.Users.FirstAsync();
        var longExchange = await db.Exchanges.FirstAsync();
        var asset = await db.Assets.FirstAsync();

        var position = new ArbitragePosition
        {
            UserId = user.Id,
            AssetId = asset.Id,
            LongExchangeId = longExchange.Id,
            ShortExchangeId = longExchange.Id,
            Status = PositionStatus.Opening,
            SizeUsdc = 500m,
            MarginUsdc = 100m,
            Leverage = 5,
            LongEntryPrice = 1_500m,
            ShortEntryPrice = 1_500m,
            EntrySpreadPerHour = 0.001m,
            CurrentSpreadPerHour = 0.001m,
            OpenedAt = DateTime.UtcNow.AddMinutes(-5),
            OpenAttemptN = openAttemptN,
            RowVersion = Array.Empty<byte>(),
        };
        db.ArbitragePositions.Add(position);
        await db.SaveChangesAsync();
        return position.Id;
    }

    [Fact]
    public async Task RunBootSweepAsync_BlocksWhenCycleLockHeld()
    {
        var dbName = $"BootSweepLock_{Guid.NewGuid()}";
        await using var db = CreateContext(dbName);
        await SeedReferenceDataAsync(db);

        var stub = new CapturingExecutionEngineStub();
        var orchestrator = CreateOrchestrator(db, stub);

        // Acquire _cycleLock via reflection so RunBootSweepAsync's WaitAsync(ct) blocks.
        var cycleLockField = typeof(BotOrchestrator)
            .GetField("_cycleLock", BindingFlags.NonPublic | BindingFlags.Instance);
        cycleLockField.Should().NotBeNull("BotOrchestrator._cycleLock must be reachable via reflection");
        var cycleLock = (SemaphoreSlim)cycleLockField!.GetValue(orchestrator)!;
        await cycleLock.WaitAsync();

        try
        {
            // Act: start RunBootSweepAsync — should block until the lock is released.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var sweepTask = orchestrator.RunBootSweepAsync(cts.Token);

            // Assert it does NOT complete within 250 ms (lock holds it).
            var firstWinner = await Task.WhenAny(sweepTask, Task.Delay(250, cts.Token));
            firstWinner.Should().NotBeSameAs(sweepTask,
                "RunBootSweepAsync must block while another holder owns _cycleLock");

            // Release the lock — sweep should complete within 2 s.
            cycleLock.Release();
            var secondWinner = await Task.WhenAny(sweepTask, Task.Delay(2000, cts.Token));
            secondWinner.Should().BeSameAs(sweepTask,
                "RunBootSweepAsync should complete after _cycleLock is released");
            await sweepTask; // surface any inner exception
        }
        finally
        {
            // Defensive: re-release if test failed before releasing.
            if (cycleLock.CurrentCount == 0)
            {
                cycleLock.Release();
            }
        }
    }

    [Fact]
    public async Task RunBootSweepAsync_AfterPriorRollback_ReusesPersistedOpenAttemptN()
    {
        var dbName = $"BootSweepAttemptN_{Guid.NewGuid()}";
        await using var db = CreateContext(dbName);
        await SeedReferenceDataAsync(db);

        // Seed a position with OpenAttemptN = 2 (simulates crash mid-rollback after first cycle).
        await SeedOpeningPositionWithAttemptCountAsync(db, openAttemptN: 2);

        var stub = new CapturingExecutionEngineStub();
        var orchestrator = CreateOrchestrator(db, stub);

        // Act: run the boot sweep — stub captures the position passed to ConfirmOrRollbackAsync.
        await orchestrator.RunBootSweepAsync(CancellationToken.None);

        // Assert: the captured position had OpenAttemptN = 2 at the time of the call.
        stub.LastReceivedPosition.Should().NotBeNull(
            "boot sweep must call ConfirmOrRollbackAsync for the Opening position");
        stub.LastReceivedPosition!.OpenAttemptN.Should().Be(2,
            "a fresh process must read the persisted OpenAttemptN from the DB, not default to 0 or 1");
    }
}
