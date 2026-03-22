using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Web.ViewModels;

namespace FundingRateArb.Web.Controllers;

[Authorize]
public class DashboardController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly ILogger<DashboardController> _logger;
    private readonly ISignalEngine _signalEngine;
    private readonly IBotControl _botControl;
    private readonly IUserSettingsService _userSettings;

    public DashboardController(
        IUnitOfWork uow,
        ILogger<DashboardController> logger,
        ISignalEngine signalEngine,
        IBotControl botControl,
        IUserSettingsService userSettings)
    {
        _uow = uow;
        _logger = logger;
        _signalEngine = signalEngine;
        _botControl = botControl;
        _userSettings = userSettings;
    }

    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        // Lazy initialization: ensure user has default settings on first visit
        var userConfig = await _userSettings.GetOrCreateConfigAsync(userId);
        var enabledExchangeIdsForInit = await _userSettings.GetUserEnabledExchangeIdsAsync(userId);
        if (enabledExchangeIdsForInit.Count == 0)
        {
            await _userSettings.InitializeDefaultsForNewUserAsync(userId);
        }

        var botConfig = await _uow.BotConfig.GetActiveAsync();
        var allOpenPositions = await _uow.Positions.GetOpenAsync();
        var openPositions = User.IsInRole("Admin")
            ? allOpenPositions
            : allOpenPositions.Where(p => p.UserId == userId).ToList();
        var unreadAlerts = await _uow.Alerts.GetByUserAsync(userId, unreadOnly: true);
        var result = await _signalEngine.GetOpportunitiesWithDiagnosticsAsync(ct);
        var allOpportunities = result.Opportunities;

        // Filter opportunities by user's enabled exchanges and assets (non-admin)
        List<ArbitrageOpportunityDto> opportunities;
        if (User.IsInRole("Admin"))
        {
            opportunities = allOpportunities;
        }
        else
        {
            var enabledExchangeIds = (await _userSettings.GetUserEnabledExchangeIdsAsync(userId)).ToHashSet();
            var enabledAssetIds = (await _userSettings.GetUserEnabledAssetIdsAsync(userId)).ToHashSet();

            opportunities = allOpportunities
                .Where(o => enabledExchangeIds.Contains(o.LongExchangeId)
                         && enabledExchangeIds.Contains(o.ShortExchangeId))
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
                : result.Diagnostics.BestRawSpread;

        // Compute PnL progress for positions when adaptive hold is enabled
        var pnlProgress = new Dictionary<int, decimal>();
        if (botConfig is not null && botConfig.AdaptiveHoldEnabled)
        {
            foreach (var pos in openPositions)
            {
                if (pos.AccumulatedFunding > 0 && pos.SizeUsdc > 0)
                {
                    var fee = pos.SizeUsdc * pos.Leverage * 2m * Application.Services.PositionHealthMonitor.GetTakerFeeRate(
                        pos.LongExchange?.Name, pos.ShortExchange?.Name);
                    var target = botConfig.TargetPnlMultiplier * fee;
                    if (target > 0)
                        pnlProgress[pos.Id] = Math.Min(pos.AccumulatedFunding / target, 2.0m);
                }
            }
        }

        var vm = new DashboardViewModel
        {
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
