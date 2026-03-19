using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Web.ViewModels;

namespace FundingRateArb.Web.Controllers;

[Authorize]
public class DashboardController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly ILogger<DashboardController> _logger;

    public DashboardController(IUnitOfWork uow, ILogger<DashboardController> logger)
    {
        _uow = uow;
        _logger = logger;
    }

    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

        var botConfig = await _uow.BotConfig.GetActiveAsync();
        var allOpenPositions = await _uow.Positions.GetOpenAsync();
        var openPositions = User.IsInRole("Admin")
            ? allOpenPositions
            : allOpenPositions.Where(p => p.UserId == userId).ToList();
        var latestRates = await _uow.FundingRates.GetLatestPerExchangePerAssetAsync();
        var unreadAlerts = await _uow.Alerts.GetByUserAsync(userId, unreadOnly: true);

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

        var rateDtos = latestRates.Select(r => new FundingRateDto
        {
            ExchangeName = r.Exchange?.Name ?? string.Empty,
            Symbol = r.Asset?.Symbol ?? string.Empty,
            RatePerHour = r.RatePerHour,
            RawRate = r.RawRate,
            MarkPrice = r.MarkPrice,
            IndexPrice = r.IndexPrice,
            Volume24hUsd = r.Volume24hUsd
        }).ToList();

        var totalPnl = positionSummaries.Sum(p => p.AccumulatedFunding);
        var bestSpread = positionSummaries.Count > 0
            ? positionSummaries.Max(p => p.CurrentSpreadPerHour)
            : (rateDtos.Count > 0 ? rateDtos.Max(r => r.RatePerHour) : 0m);

        var vm = new DashboardViewModel
        {
            BotEnabled = botConfig?.IsEnabled ?? false,
            OpenPositionCount = openPositions.Count,
            TotalPnl = totalPnl,
            BestSpread = bestSpread,
            TotalUnreadAlerts = unreadAlerts.Count,
            LatestRates = rateDtos,
            OpenPositions = positionSummaries
        };

        return View(vm);
    }
}
