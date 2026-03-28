using System.Security.Claims;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
                BestSpread = bestSpreadAnon,
                Opportunities = anonOpportunities,
                Diagnostics = null,
            };

            return View(anonVm);
        }

        // Authenticated path: parallelize independent async calls to reduce total latency
        var resultTask = _signalEngine.GetOpportunitiesWithDiagnosticsAsync(ct);
        var botConfigTask = _uow.BotConfig.GetActiveAsync();
        var userConfigTask = _userSettings.GetOrCreateConfigAsync(userId!);
        var exchangeIdsTask = _userSettings.GetUserEnabledExchangeIdsAsync(userId!);
        // NB4: Admin loads all positions; non-admin pushes user filter to SQL
        var positionsTask = User.IsInRole("Admin")
            ? _uow.Positions.GetOpenAsync()
            : _uow.Positions.GetOpenByUserAsync(userId!);
        var alertsTask = _uow.Alerts.GetByUserAsync(userId!, unreadOnly: true);

        await Task.WhenAll(resultTask, botConfigTask, userConfigTask, exchangeIdsTask, positionsTask, alertsTask);

        var result = await resultTask;
        var allOpportunities = result.Opportunities;
        var botConfig = await botConfigTask;
        var userConfig = await userConfigTask;
        var enabledExchangeIds = await exchangeIdsTask;
        var openPositions = await positionsTask;
        var unreadAlerts = await alertsTask;

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

        var positionSummaries = openPositions.Select(p => new PositionSummaryDto
        {
            Id = p.Id,
            AssetSymbol = p.Asset?.Symbol ?? string.Empty,
            LongExchangeName = p.LongExchange?.Name ?? string.Empty,
            ShortExchangeName = p.ShortExchange?.Name ?? string.Empty,
            SizeUsdc = p.SizeUsdc,
            MarginUsdc = p.MarginUsdc,
            EntrySpreadPerHour = p.EntrySpreadPerHour,
            CurrentSpreadPerHour = p.CurrentSpreadPerHour,
            AccumulatedFunding = p.AccumulatedFunding,
            RealizedPnl = p.RealizedPnl,
            Status = p.Status,
            OpenedAt = p.OpenedAt,
            ClosedAt = p.ClosedAt
        }).ToList();

        var totalPnl = positionSummaries.Sum(p => p.AccumulatedFunding);
        var bestSpread = positionSummaries.Count > 0
            ? positionSummaries.Max(p => p.CurrentSpreadPerHour)
            : opportunities.Count > 0
                ? opportunities.Max(o => o.SpreadPerHour)
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
            BotEnabled = botConfig?.IsEnabled ?? false,
            OpenPositionCount = openPositions.Count,
            TotalPnl = totalPnl,
            BestSpread = bestSpread,
            TotalUnreadAlerts = unreadAlerts.Count,
            OpenPositions = positionSummaries,
            Opportunities = opportunities,
            Diagnostics = result.Diagnostics,
            AdaptiveHoldEnabled = botConfig?.AdaptiveHoldEnabled ?? false,
            RebalanceEnabled = botConfig?.RebalanceEnabled ?? false,
            PnlProgressByPosition = pnlProgress,
        };

        if (User.IsInRole("Admin") && botConfig is not null)
        {
            vm.NotionalPerLeg = botConfig.TotalCapitalUsdc * botConfig.MaxCapitalPerPosition * botConfig.DefaultLeverage;
            vm.VolumeFraction = botConfig.VolumeFraction;
        }

        return View(vm);
    }

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
