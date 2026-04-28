using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Web.ViewModels.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FundingRateArb.Web.Areas.Admin.Controllers;

[Area("Admin")]
[Authorize(Roles = "Admin")]
public class PairDenyListController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly IPairDenyListProvider _provider;
    private readonly ILogger<PairDenyListController> _logger;

    public PairDenyListController(IUnitOfWork uow, IPairDenyListProvider provider, ILogger<PairDenyListController> logger)
    {
        _uow = uow;
        _provider = provider;
        _logger = logger;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var rows = await _uow.PairExecutionStats.GetAllAsync(ct);
        var config = await _uow.BotConfig.GetActiveAsync();
        var vm = new PairDenyListViewModel
        {
            Rows = rows.ConvertAll(r => new PairDenyListRow
            {
                LongExchangeName = r.LongExchangeName,
                ShortExchangeName = r.ShortExchangeName,
                CloseCount = r.CloseCount,
                WinCount = r.WinCount,
                TotalPnlUsdc = r.TotalPnlUsdc,
                AvgHoldSec = r.AvgHoldSec,
                IsDenied = r.IsDenied,
                DeniedUntil = r.DeniedUntil,
                DeniedReason = r.DeniedReason,
                LastUpdatedAt = r.LastUpdatedAt,
            }),
            SnapshotAt = _provider.Current.SnapshotAt,
            DeniedCount = _provider.Current.Count,
            AutoDenyEnabled = config.PairAutoDenyEnabled,
        };
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Deny(string longExchangeName, string shortExchangeName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(longExchangeName) || string.IsNullOrWhiteSpace(shortExchangeName))
        {
            TempData["Error"] = "Both exchange names are required.";
            return RedirectToAction(nameof(Index));
        }
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown";
        var existing = await _uow.PairExecutionStats.GetByPairAsync(longExchangeName, shortExchangeName, ct);
        var row = existing ?? new PairExecutionStats
        {
            LongExchangeName = longExchangeName,
            ShortExchangeName = shortExchangeName,
            WindowStart = DateTime.UtcNow.AddDays(-14),
            WindowEnd = DateTime.UtcNow,
        };
        row.IsDenied = true;
        row.DeniedUntil = null;   // indefinite manual deny
        row.DeniedReason = $"manual: {userId}";
        row.LastUpdatedAt = DateTime.UtcNow;
        await _uow.PairExecutionStats.UpsertAsync(row, ct);
        await _uow.SaveAsync(ct);
        await _provider.RefreshAsync(ct);
        _logger.LogWarning("Admin manual deny: {Long}/{Short} by {AdminUserId}", longExchangeName, shortExchangeName, userId);
        TempData["Success"] = $"Pair {longExchangeName}/{shortExchangeName} denied.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UnDeny(string longExchangeName, string shortExchangeName, CancellationToken ct)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown";
        var existing = await _uow.PairExecutionStats.GetByPairAsync(longExchangeName, shortExchangeName, ct);
        if (existing is null)
        {
            TempData["Error"] = $"No row found for {longExchangeName}/{shortExchangeName}.";
            return RedirectToAction(nameof(Index));
        }
        existing.IsDenied = false;
        existing.DeniedUntil = null;
        existing.DeniedReason = null;
        existing.LastUpdatedAt = DateTime.UtcNow;
        await _uow.PairExecutionStats.UpsertAsync(existing, ct);
        await _uow.SaveAsync(ct);
        await _provider.RefreshAsync(ct);
        _logger.LogWarning("Admin manual un-deny: {Long}/{Short} by {AdminUserId}", longExchangeName, shortExchangeName, userId);
        TempData["Success"] = $"Pair {longExchangeName}/{shortExchangeName} un-denied.";
        return RedirectToAction(nameof(Index));
    }
}
