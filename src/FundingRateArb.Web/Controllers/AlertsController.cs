using System.Security.Claims;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
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
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Unauthorized();
        }

        var alerts = User.IsInRole("Admin")
            ? await _uow.Alerts.GetAllAsync()
            : await _uow.Alerts.GetByUserAsync(userId);

        var alertDtos = alerts.Select(a => new AlertDto
        {
            Id = a.Id,
            UserId = a.UserId,
            ArbitragePositionId = a.ArbitragePositionId,
            Type = a.Type,
            Severity = a.Severity,
            Message = a.Message,
            IsRead = a.IsRead,
            CreatedAt = a.CreatedAt,
        }).ToList();

        var vm = new AlertIndexViewModel
        {
            Alerts = alertDtos,
            UnreadCount = alertDtos.Count(a => !a.IsRead),
        };

        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAllRead()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await _uow.Alerts.MarkAllReadAsync(userId);
        await _uow.SaveAsync();
        TempData["Success"] = "All alerts marked as read.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAsRead(int id, CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Unauthorized();
        }

        var alert = await _uow.Alerts.GetByIdAsync(id);

        if (alert is null)
        {
            return NotFound();
        }

        if (!User.IsInRole("Admin") && alert.UserId != userId)
        {
            return Forbid();
        }

        alert.IsRead = true;
        _uow.Alerts.Update(alert);
        await _uow.SaveAsync(ct);

        return RedirectToAction(nameof(Index));
    }
}
