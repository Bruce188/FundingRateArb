using System.Security.Claims;
using FundingRateArb.Application.Common;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Extensions;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;

namespace FundingRateArb.Web.Controllers;

[Authorize]
public class DashboardController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly ILogger<DashboardController> _logger;
    private readonly ISignalEngine _signalEngine;
    private readonly IBotControl _botControl;
    private readonly IUserSettingsService _userSettings;
    private readonly IMemoryCache _cache;

    private const string AnonymousOpportunityCacheKey = "dashboard:anonymous:opportunities";

    public DashboardController(
        IUnitOfWork uow,
        ILogger<DashboardController> logger,
        ISignalEngine signalEngine,
        IBotControl botControl,
        IUserSettingsService userSettings,
        IMemoryCache cache)
    {
        _uow = uow;
        _logger = logger;
        _signalEngine = signalEngine;
        _botControl = botControl;
        _userSettings = userSettings;
        _cache = cache;
    }

    [AllowAnonymous]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var isAuthenticated = userId is not null;

        if (!isAuthenticated)
        {
            // Anonymous path: cache opportunity result with 30-second TTL.
            // Note: MemoryCache.GetOrCreateAsync does not provide per-key locking in .NET 8.
            // Under concurrent requests during TTL gap, the factory may execute multiple times.
            // Acceptable given the 30s TTL and low anonymous traffic volume.
            var cachedResult = await _cache.GetOrCreateAsync(AnonymousOpportunityCacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
                return await _signalEngine.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);
            });

            var anonOpportunities = cachedResult!.Opportunities;
            var bestSpreadAnon = anonOpportunities.Count > 0
                ? anonOpportunities.Max(o => o.SpreadPerHour)
                : cachedResult.Diagnostics?.BestRawSpread ?? 0m;

            // Design decision: full opportunity data is shown to anonymous visitors as a public showcase. See review-v45 NB3.
            var anonVm = new DashboardViewModel
            {
                IsAuthenticated = false,
                BotEnabled = false, // NB2: never expose bot status to anonymous visitors
                OperatingState = "Stopped",
                BestSpread = bestSpreadAnon,
                Opportunities = anonOpportunities,
                Diagnostics = null,
                DatabaseAvailable = cachedResult.DatabaseAvailable,
            };

            return View(anonVm);
        }

        // All queries sequential — DbContext is not thread-safe (scoped UoW shared across services).
        // Any DatabaseUnavailableException or transient SqlException propagated by the
        // downstream repositories short-circuits into a degraded view with a banner
        // instead of a 500 page. This is the B1/B4 fix from review-v131 — the previous
        // pass only fixed the first repository call, but the dashboard makes ~7 more DB
        // calls that can all fail during a sustained outage.
        try
        {
            var result = await _signalEngine.GetOpportunitiesWithDiagnosticsAsync(ct);

            // B1: short-circuit immediately when the SignalEngine already detected a
            // degraded database — every subsequent DB call below will rethrow the same
            // failure and tear the whole request back to 500 if we proceed.
            if (!result.DatabaseAvailable)
            {
                return DegradedDashboardView();
            }

            var allOpportunities = result.Opportunities;
            var botConfig = await _uow.BotConfig.GetActiveAsync();
            var userConfig = await _userSettings.GetOrCreateConfigAsync(userId!);
            var enabledExchangeIds = await _userSettings.GetUserEnabledExchangeIdsAsync(userId!);
            // NB4: Admin loads all positions; non-admin pushes user filter to SQL
            var openPositions = User.IsInRole("Admin")
                ? await _uow.Positions.GetOpenAsync()
                : await _uow.Positions.GetOpenByUserAsync(userId!);
            var unreadAlerts = await _uow.Alerts.GetByUserAsync(userId!, unreadOnly: true);

            // Lazy initialization: ensure user has default settings on first visit
            if (enabledExchangeIds.Count == 0)
            {
                await _userSettings.InitializeDefaultsForNewUserAsync(userId!);
                enabledExchangeIds = await _userSettings.GetUserEnabledExchangeIdsAsync(userId!);
            }

            // Filter opportunities by user's enabled exchanges and assets (non-admin)
            List<ArbitrageOpportunityDto> opportunities;
            if (User.IsInRole("Admin"))
            {
                opportunities = allOpportunities;
            }
            else
            {
                var enabledExchangeIdSet = enabledExchangeIds.ToHashSet();
                var dataOnlyExchangeIds = await _userSettings.GetDataOnlyExchangeIdsAsync();
                enabledExchangeIdSet.UnionWith(dataOnlyExchangeIds);
                var enabledAssetIds = (await _userSettings.GetUserEnabledAssetIdsAsync(userId!)).ToHashSet();

                opportunities = allOpportunities
                    .Where(o => enabledExchangeIdSet.Contains(o.LongExchangeId)
                             && enabledExchangeIdSet.Contains(o.ShortExchangeId))
                    .Where(o => enabledAssetIds.Contains(o.AssetId))
                    .ToList();
            }

            var openingCount = await _uow.Positions.CountByStatusAsync(PositionStatus.Opening);
            var needsAttentionCount = await _uow.Positions.CountByStatusesAsync(PositionStatus.EmergencyClosed, PositionStatus.Failed);

            var positionSummaries = openPositions.Select(p => p.ToSummaryDto()).ToList();

            var totalPnl = positionSummaries.Sum(p => p.AccumulatedFunding);
            var bestSpread = opportunities.Count > 0
                ? opportunities.Max(o => o.SpreadPerHour)
                : positionSummaries.Count > 0
                    ? positionSummaries.Max(p => p.CurrentSpreadPerHour)
                    : result.Diagnostics?.BestRawSpread ?? 0m;

            // Compute PnL progress for positions when adaptive hold is enabled
            var pnlProgress = new Dictionary<int, decimal>();
            if (botConfig is not null && botConfig.AdaptiveHoldEnabled)
            {
                foreach (var pos in openPositions)
                {
                    if (pos.AccumulatedFunding > 0 && pos.SizeUsdc > 0)
                    {
                        var fee = pos.EntryFeesUsdc > 0
                            ? pos.EntryFeesUsdc
                            : pos.SizeUsdc * pos.Leverage * 2m * Application.Services.PositionHealthMonitor.GetTakerFeeRate(
                                pos.LongExchange?.Name, pos.ShortExchange?.Name,
                                pos.LongExchange?.TakerFeeRate, pos.ShortExchange?.TakerFeeRate);
                        var target = botConfig.TargetPnlMultiplier * fee;
                        if (target > 0)
                        {
                            pnlProgress[pos.Id] = Math.Min(pos.AccumulatedFunding / target, 2.0m);
                        }
                    }
                }
            }

            var vm = new DashboardViewModel
            {
                IsAuthenticated = true,
                BotEnabled = botConfig?.OperatingState is BotOperatingState.Armed or BotOperatingState.Trading,
                OperatingState = botConfig?.OperatingState.ToString() ?? "Stopped",
                OpenPositionCount = openPositions.Count,
                OpeningPositionCount = openingCount,
                NeedsAttentionCount = needsAttentionCount,
                TotalPnl = totalPnl,
                BestSpread = bestSpread,
                TotalUnreadAlerts = unreadAlerts.Count,
                OpenPositions = positionSummaries,
                Opportunities = opportunities,
                Diagnostics = result.Diagnostics,
                AdaptiveHoldEnabled = botConfig?.AdaptiveHoldEnabled ?? false,
                RebalanceEnabled = botConfig?.RebalanceEnabled ?? false,
                PnlProgressByPosition = pnlProgress,
                DatabaseAvailable = result.DatabaseAvailable,
            };

            if (User.IsInRole("Admin") && botConfig is not null)
            {
                vm.NotionalPerLeg = botConfig.TotalCapitalUsdc * botConfig.MaxCapitalPerPosition * botConfig.DefaultLeverage;
                vm.VolumeFraction = botConfig.VolumeFraction;
            }

            return View(vm);
        }
        catch (DatabaseUnavailableException ex)
        {
            _logger.LogWarning(ex, "Dashboard rendered in degraded state — database temporarily unavailable");
            return DegradedDashboardView();
        }
        catch (SqlException ex) when (SqlTransientErrorNumbers.Contains(ex.Number))
        {
            // B4: catch transient SqlExceptions from the non-wrapped repository calls
            // (BotConfig / Positions / Alerts / userSettings) so the dashboard still
            // renders a banner rather than a 500 page during a sustained DB outage.
            _logger.LogWarning(
                "Dashboard rendered in degraded state — SQL transient failure Number={Number} Type={Type}",
                ex.Number, ex.GetType().Name);
            return DegradedDashboardView();
        }
    }

    /// <summary>
    /// Render the dashboard in degraded mode — empty collections, DatabaseAvailable=false
    /// so the view shows the "Data source unavailable" banner from review-v131 B1.
    /// </summary>
    private ViewResult DegradedDashboardView() =>
        View(nameof(Index), new DashboardViewModel
        {
            IsAuthenticated = true,
            BotEnabled = false,
            OperatingState = "Unknown",
            DatabaseAvailable = false,
            Opportunities = [],
            OpenPositions = [],
            BestSpread = 0m,
        });

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin")]
    public IActionResult RetryNow()
    {
        _botControl.ClearCooldowns();
        _botControl.TriggerImmediateCycle();
        return Json(new { success = true, message = "Cooldowns cleared — next cycle triggered." });
    }
}
