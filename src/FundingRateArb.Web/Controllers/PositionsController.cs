using System.Security.Claims;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Extensions;
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
    private readonly IExchangeConnectorFactory _connectorFactory;
    private readonly ILogger<PositionsController> _logger;

    public PositionsController(IUnitOfWork uow, IExecutionEngine executionEngine,
        IExchangeConnectorFactory connectorFactory,
        ILogger<PositionsController> logger)
    {
        _uow = uow;
        _executionEngine = executionEngine;
        _connectorFactory = connectorFactory;
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

        var positionDtos = positions.Select(p => p.ToSummaryDto()).ToList();

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

        var positionDto = position.ToDetailsDto();

        // Populate margin utilization fields from exchange API (best-effort)
        if (position.Status == PositionStatus.Open)
        {
            try
            {
                var longExchangeName = position.LongExchange?.Name;
                var shortExchangeName = position.ShortExchange?.Name;
                var assetSymbol = position.Asset?.Symbol;

                if (longExchangeName is not null && shortExchangeName is not null && assetSymbol is not null)
                {
                    // Dedupe by exchange name (the source of truth) — not by reference equality
                    // on the connector instance, which would silently break if a future factory
                    // implementation returned different instances for the same exchange name.
                    var sameExchange = string.Equals(longExchangeName, shortExchangeName, StringComparison.OrdinalIgnoreCase);
                    var longConnector = _connectorFactory.GetConnector(longExchangeName);
                    var shortConnector = sameExchange
                        ? longConnector
                        : _connectorFactory.GetConnector(shortExchangeName);

                    // Fetch margin state AND current mark prices in parallel.
                    // Using current marks (not entry prices) for MaxSafeMovePct reflects the
                    // actual remaining buffer as price moves — otherwise the value stays frozen
                    // at entry and misrepresents risk during adverse moves.
                    Task<MarginStateDto?> longMarginTask;
                    Task<MarginStateDto?> shortMarginTask;
                    if (sameExchange)
                    {
                        longMarginTask = longConnector.GetPositionMarginStateAsync(assetSymbol, ct);
                        shortMarginTask = longMarginTask;
                    }
                    else
                    {
                        longMarginTask = longConnector.GetPositionMarginStateAsync(assetSymbol, ct);
                        shortMarginTask = shortConnector.GetPositionMarginStateAsync(assetSymbol, ct);
                    }

                    var longMarkTask = longConnector.GetMarkPriceAsync(assetSymbol, ct);
                    var shortMarkTask = sameExchange
                        ? longMarkTask
                        : shortConnector.GetMarkPriceAsync(assetSymbol, ct);

                    await Task.WhenAll(longMarginTask, shortMarginTask, longMarkTask, shortMarkTask);

                    var longMargin = await longMarginTask;
                    var shortMargin = await shortMarginTask;
                    var currentLongMark = await longMarkTask;
                    var currentShortMark = await shortMarkTask;

                    positionDto.LongMarginUtilizationPct = longMargin?.MarginUtilizationPct;
                    positionDto.ShortMarginUtilizationPct = shortMargin?.MarginUtilizationPct;

                    if (longMargin?.LiquidationPrice is not null && currentLongMark > 0)
                    {
                        positionDto.MaxSafeMovePctLong =
                            Math.Abs(currentLongMark - longMargin.LiquidationPrice.Value) / currentLongMark * 100m;
                    }

                    if (shortMargin?.LiquidationPrice is not null && currentShortMark > 0)
                    {
                        positionDto.MaxSafeMovePctShort =
                            Math.Abs(currentShortMark - shortMargin.LiquidationPrice.Value) / currentShortMark * 100m;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to fetch margin state for position #{Id}", position.Id);
            }
        }

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

        try
        {
            await _executionEngine.ClosePositionAsync(position.UserId, position, CloseReason.Manual, ct);
        }
        catch (OperationCanceledException)
        {
            throw; // Let ASP.NET Core handle client disconnects gracefully
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to close position {PositionId} (action by {ActingUserId})", id, userId);
            TempData["Error"] = "Failed to close position. Please try again or contact support.";
            return RedirectToAction(nameof(Index));
        }

        // Verify the close actually took effect — ClosePositionAsync may return without error
        // but leave the position in a non-closed state (e.g., partial fill, exchange rejection).
        // This relies on ClosePositionAsync mutating the passed entity in-place. If the engine's
        // DbContext scope changes, re-fetch the position after close.
        if (position.Status != PositionStatus.Closed)
        {
            _logger.LogWarning("Position {PositionId} not closed after ClosePositionAsync (status: {Status})", id, position.Status);
            TempData["Error"] = "Position close was submitted but did not complete. Check position status.";
            return RedirectToAction(nameof(Index));
        }

        // Persist a durable audit record when an admin closes a position on behalf of another user.
        // This ensures admin closures remain traceable even if logs are rotated.
        // The close operation saves internally via the execution engine's own UoW call.
        // The audit alert is a separate save because the engine's save is encapsulated and
        // cannot be extended. Both are on the same scoped DbContext, but if the audit save
        // fails after a successful close, the close still persists — acceptable since the
        // structured log above provides a fallback audit trail.
        if (User.IsInRole("Admin") && position.UserId != userId)
        {
            try
            {
                _uow.Alerts.Add(new Domain.Entities.Alert
                {
                    UserId = position.UserId,
                    ArbitragePositionId = position.Id,
                    Type = AlertType.PositionClosed,
                    Severity = AlertSeverity.Info,
                    Message = "Position manually closed by administrator.",
                    ActingUserId = userId,
                });
                await _uow.SaveAsync(ct);
            }
            catch (Exception ex)
            {
                // Audit persistence failure after a successful close should not surface as a 500.
                // The structured log above provides a fallback audit trail.
                _logger.LogError(ex, "Failed to persist audit alert for position {PositionId} closed by admin {AdminUserId}", id, userId);
            }
        }

        TempData["Success"] = "Position closed successfully.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Lazy-loaded live margin/liquidation data for the Details page.
    /// Returns partial data or error on exchange timeout.
    /// </summary>
    [HttpGet("api/positions/{id}/live-margin")]
    public async Task<IActionResult> GetLiveMargin(int id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var position = await _uow.Positions.GetByIdAsync(id);
        if (position is null) return NotFound();
        if (!User.IsInRole("Admin") && position.UserId != userId) return Forbid();

        var longExchangeName = position.LongExchange?.Name;
        var shortExchangeName = position.ShortExchange?.Name;
        var assetSymbol = position.Asset?.Symbol;

        if (longExchangeName is null || shortExchangeName is null || assetSymbol is null)
        {
            return BadRequest(new { error = "Missing exchange or asset data" });
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var sameExchange = string.Equals(longExchangeName, shortExchangeName, StringComparison.OrdinalIgnoreCase);
            var longConnector = _connectorFactory.GetConnector(longExchangeName);
            var shortConnector = sameExchange ? longConnector : _connectorFactory.GetConnector(shortExchangeName);

            var longMarginTask = longConnector.GetPositionMarginStateAsync(assetSymbol, cts.Token);
            var shortMarginTask = sameExchange ? longMarginTask : shortConnector.GetPositionMarginStateAsync(assetSymbol, cts.Token);

            await Task.WhenAll(longMarginTask, shortMarginTask);

            var longMargin = await longMarginTask;
            var shortMargin = await shortMarginTask;

            return Ok(new
            {
                longMarginUtilization = longMargin?.MarginUtilizationPct,
                shortMarginUtilization = shortMargin?.MarginUtilizationPct,
                longLiquidationPrice = longMargin?.LiquidationPrice,
                shortLiquidationPrice = shortMargin?.LiquidationPrice,
                longMarginUsed = longMargin?.MarginUsed,
                shortMarginUsed = shortMargin?.MarginUsed,
            });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(503, new { error = "Exchange data unavailable (timeout)" });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live margin fetch failed for position #{Id}", id);
            return StatusCode(503, new { error = "Exchange data unavailable" });
        }
    }
}
