using System.Security.Claims;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FundingRateArb.Web.Controllers;

[Authorize]
public class PositionsController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly IExecutionEngine _executionEngine;
    private readonly ILogger<PositionsController> _logger;

    public PositionsController(IUnitOfWork uow, IExecutionEngine executionEngine,
        ILogger<PositionsController> logger)
    {
        _uow = uow;
        _executionEngine = executionEngine;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var positions = User.IsInRole("Admin")
            ? await _uow.Positions.GetAllAsync()
            : await _uow.Positions.GetByUserAsync(userId);

        return View(new PositionIndexViewModel { Positions = positions });
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();
        var position = await _uow.Positions.GetByIdAsync(id);

        if (position is null)
            return NotFound();

        if (!User.IsInRole("Admin") && position.UserId != userId)
            return Forbid();

        var vm = new PositionDetailsViewModel
        {
            Position = position,
            UnrealizedPnl = position.AccumulatedFunding,
            DurationHours = (decimal)(DateTime.UtcNow - position.OpenedAt).TotalHours,
        };

        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(int id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();
        var position = await _uow.Positions.GetByIdAsync(id);

        if (position is null)
            return NotFound();

        if (!User.IsInRole("Admin") && position.UserId != userId)
            return Forbid();

        if (position.Status != PositionStatus.Open)
            return BadRequest("Position is already closed.");

        _logger.LogInformation("User {UserId} manually closing position {PositionId}", userId, id);
        await _executionEngine.ClosePositionAsync(position, CloseReason.Manual);
        TempData["Success"] = "Position closed successfully.";
        return RedirectToAction(nameof(Index));
    }
}
