using System.Threading;
using System.Threading.Tasks;
using FundingRateArb.Application.Common;
using FundingRateArb.Application.Services;
using FundingRateArb.Application.ViewModels;
using FundingRateArb.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Web.Areas.Admin.Controllers;

[Area("Admin")]
[Authorize(Roles = "Admin")]
public class StatusController(
    IStatusPageAggregator aggregator,
    ILogger<StatusController> logger
) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        try
        {
            var vm = await aggregator.GetAsync(ct);
            return View(vm);
        }
        catch (DatabaseUnavailableException ex)
        {
            logger.LogWarning(ex, "Status page degraded: database unavailable");
            return View(DegradedView("Database temporarily unavailable. Try again in a moment."));
        }
        catch (SqlException ex) when (SqlTransientErrorNumbers.Contains(ex.Number))
        {
            logger.LogWarning(ex, "Status page degraded: transient SQL error {Number}", ex.Number);
            return View(DegradedView("Database experienced a transient error. Try again in a moment."));
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            // Note: the aggregator swaps CancellationToken.None before calling BuildAsync,
            // so OperationCanceledException cannot bubble from the aggregator itself.
            // This catch handles the Razor view-rendering path only.
            logger.LogDebug("Status page request cancelled by client");
            return new EmptyResult();
        }
    }

    private static StatusViewModel DegradedView(string reason) => new()
    {
        DatabaseAvailable = false,
        DegradedReason = reason,
    };
}
