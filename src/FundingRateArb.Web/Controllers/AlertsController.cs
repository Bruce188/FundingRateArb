using System.Security.Claims;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FundingRateArb.Web.Controllers;

[Authorize]
public class AlertsController : Controller
{
    private readonly IUnitOfWork _uow;

    public AlertsController(IUnitOfWork uow)
    {
        _uow = uow;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var alerts = User.IsInRole("Admin")
            ? await _uow.Alerts.GetAllAsync()
            : await _uow.Alerts.GetByUserAsync(userId);

        var vm = new AlertIndexViewModel
        {
            Alerts = alerts,
            UnreadCount = alerts.Count(a => !a.IsRead),
        };

        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAsRead(int id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();
        var alert = await _uow.Alerts.GetByIdAsync(id);

        if (alert is null) return NotFound();

        if (!User.IsInRole("Admin") && alert.UserId != userId)
            return Forbid();

        alert.IsRead = true;
        _uow.Alerts.Update(alert);
        await _uow.SaveAsync();

        return RedirectToAction(nameof(Index));
    }
}
