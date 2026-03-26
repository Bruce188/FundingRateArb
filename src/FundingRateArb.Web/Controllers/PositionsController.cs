using System.Security.Claims;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
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
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Unauthorized();
        }

        var positions = User.IsInRole("Admin")
            ? await _uow.Positions.GetAllAsync()
            : await _uow.Positions.GetByUserAsync(userId);

        var positionDtos = positions.Select(p => new PositionSummaryDto
        {
            Id = p.Id,
            AssetSymbol = p.Asset?.Symbol ?? $"Asset #{p.AssetId}",
            LongExchangeName = p.LongExchange?.Name ?? $"Exchange #{p.LongExchangeId}",
            ShortExchangeName = p.ShortExchange?.Name ?? $"Exchange #{p.ShortExchangeId}",
            SizeUsdc = p.SizeUsdc,
            MarginUsdc = p.MarginUsdc,
            EntrySpreadPerHour = p.EntrySpreadPerHour,
            CurrentSpreadPerHour = p.CurrentSpreadPerHour,
            AccumulatedFunding = p.AccumulatedFunding,
            UnrealizedPnl = p.AccumulatedFunding,
            RealizedPnl = p.RealizedPnl,
            Status = p.Status,
            OpenedAt = p.OpenedAt,
            ClosedAt = p.ClosedAt,
        }).ToList();

        return View(new PositionIndexViewModel { Positions = positionDtos });
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Unauthorized();
        }

        var position = await _uow.Positions.GetByIdAsync(id);

        if (position is null)
        {
            return NotFound();
        }

        if (!User.IsInRole("Admin") && position.UserId != userId)
        {
            return Forbid();
        }

        var positionDto = new PositionDetailsDto
        {
            Id = position.Id,
            AssetSymbol = position.Asset?.Symbol ?? $"Asset #{position.AssetId}",
            AssetId = position.AssetId,
            LongExchangeName = position.LongExchange?.Name ?? $"Exchange #{position.LongExchangeId}",
            LongExchangeId = position.LongExchangeId,
            ShortExchangeName = position.ShortExchange?.Name ?? $"Exchange #{position.ShortExchangeId}",
            ShortExchangeId = position.ShortExchangeId,
            SizeUsdc = position.SizeUsdc,
            MarginUsdc = position.MarginUsdc,
            Leverage = position.Leverage,
            LongEntryPrice = position.LongEntryPrice,
            ShortEntryPrice = position.ShortEntryPrice,
            EntrySpreadPerHour = position.EntrySpreadPerHour,
            CurrentSpreadPerHour = position.CurrentSpreadPerHour,
            AccumulatedFunding = position.AccumulatedFunding,
            RealizedPnl = position.RealizedPnl,
            Status = position.Status,
            CloseReason = position.CloseReason,
            OpenedAt = position.OpenedAt,
            ClosedAt = position.ClosedAt,
            Notes = position.Notes,
        };

        var vm = new PositionDetailsViewModel
        {
            Position = positionDto,
            UnrealizedPnl = position.AccumulatedFunding,
            DurationHours = (decimal)(DateTime.UtcNow - position.OpenedAt).TotalHours,
        };

        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(int id, CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Unauthorized();
        }

        var position = await _uow.Positions.GetByIdAsync(id);

        if (position is null)
        {
            return NotFound();
        }

        if (!User.IsInRole("Admin") && position.UserId != userId)
        {
            return Forbid();
        }

        if (position.Status != PositionStatus.Open)
        {
            return BadRequest("Position is already closed.");
        }

        // Admin close intentionally uses the position owner's credentials (admin acts on behalf of user).
        // The audit log records both the acting admin and the position owner for accountability.
        _logger.LogInformation("User {ActingUserId} closing position {PositionId} owned by {OwnerUserId}", userId, id, position.UserId);
        await _executionEngine.ClosePositionAsync(position.UserId, position, CloseReason.Manual, ct);
        TempData["Success"] = "Position closed successfully.";
        return RedirectToAction(nameof(Index));
    }
}
