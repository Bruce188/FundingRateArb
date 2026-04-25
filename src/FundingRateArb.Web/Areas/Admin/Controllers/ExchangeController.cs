using System.Security.Claims;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Web.ViewModels.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace FundingRateArb.Web.Areas.Admin.Controllers;

[Area("Admin")]
[Authorize(Roles = "Admin")]
public class ExchangeController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly ILogger<ExchangeController> _logger;

    public ExchangeController(IUnitOfWork uow, ILogger<ExchangeController> logger)
    {
        _uow = uow;
        _logger = logger;
    }

    public async Task<IActionResult> Index()
    {
        var exchanges = await _uow.Exchanges.GetAllAsync();
        return View(exchanges);
    }

    public IActionResult Create()
    {
        return View(new ExchangeCreateViewModel
        {
            FundingIntervalOptions = GetFundingIntervalOptions()
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ExchangeCreateViewModel model)
    {
        if (!Uri.TryCreate(model.ApiBaseUrl, UriKind.Absolute, out var apiUri)
            || apiUri.Scheme != "https")
        {
            ModelState.AddModelError(nameof(model.ApiBaseUrl), "Must be a valid HTTPS URL.");
        }
        if (!Uri.TryCreate(model.WsBaseUrl, UriKind.Absolute, out var wsUri)
            || wsUri.Scheme != "wss")
        {
            ModelState.AddModelError(nameof(model.WsBaseUrl), "Must be a valid secure WebSocket URL (wss://).");
        }

        if (!ModelState.IsValid)
        {
            model.FundingIntervalOptions = GetFundingIntervalOptions();
            return View(model);
        }

        var exchange = new Exchange
        {
            Name = model.Name,
            ApiBaseUrl = model.ApiBaseUrl,
            WsBaseUrl = model.WsBaseUrl,
            FundingInterval = model.FundingInterval!.Value,
            FundingIntervalHours = model.FundingIntervalHours ?? 1,
            SupportsSubAccounts = model.SupportsSubAccounts,
            Description = model.Description,
            IsActive = true,
            FundingRebateRate = model.FundingRebateRate,
            FundingTimingDeviationSeconds = model.FundingTimingDeviationSeconds,
            FundingNotionalPriceType = model.FundingNotionalPriceType
        };

        _uow.Exchanges.Add(exchange);
        await _uow.SaveAsync();
        _uow.Exchanges.InvalidateCache();

        _logger.LogWarning("Admin {Action}: {EntityType} {EntityId} by {AdminUserId}",
            "Created", "Exchange", exchange.Id, User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown");

        TempData["Success"] = $"Exchange '{exchange.Name}' created successfully.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id)
    {
        var exchange = await _uow.Exchanges.GetByIdAsync(id);
        if (exchange is null)
        {
            return NotFound();
        }

        var model = new ExchangeEditViewModel
        {
            Id = exchange.Id,
            Name = exchange.Name,
            ApiBaseUrl = exchange.ApiBaseUrl,
            WsBaseUrl = exchange.WsBaseUrl,
            FundingInterval = exchange.FundingInterval,
            FundingIntervalHours = exchange.FundingIntervalHours,
            SupportsSubAccounts = exchange.SupportsSubAccounts,
            IsActive = exchange.IsActive,
            Description = exchange.Description,
            FundingRebateRate = exchange.FundingRebateRate,
            FundingTimingDeviationSeconds = exchange.FundingTimingDeviationSeconds,
            FundingNotionalPriceType = exchange.FundingNotionalPriceType,
            FundingIntervalOptions = GetFundingIntervalOptions()
        };

        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ExchangeEditViewModel model)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        if (!Uri.TryCreate(model.ApiBaseUrl, UriKind.Absolute, out var apiUri)
            || apiUri.Scheme != "https")
        {
            ModelState.AddModelError(nameof(model.ApiBaseUrl), "Must be a valid HTTPS URL.");
        }
        if (!Uri.TryCreate(model.WsBaseUrl, UriKind.Absolute, out var wsUri)
            || wsUri.Scheme != "wss")
        {
            ModelState.AddModelError(nameof(model.WsBaseUrl), "Must be a valid secure WebSocket URL (wss://).");
        }

        if (!ModelState.IsValid)
        {
            model.FundingIntervalOptions = GetFundingIntervalOptions();
            return View(model);
        }

        var exchange = await _uow.Exchanges.GetByIdAsync(id);
        if (exchange is null)
        {
            return NotFound();
        }

        exchange.Name = model.Name;
        exchange.ApiBaseUrl = model.ApiBaseUrl;
        exchange.WsBaseUrl = model.WsBaseUrl;
        exchange.FundingInterval = model.FundingInterval!.Value;
        exchange.FundingIntervalHours = model.FundingIntervalHours ?? 1;
        exchange.SupportsSubAccounts = model.SupportsSubAccounts;
        exchange.IsActive = model.IsActive;
        exchange.Description = model.Description;
        exchange.FundingRebateRate = model.FundingRebateRate;
        exchange.FundingTimingDeviationSeconds = model.FundingTimingDeviationSeconds;
        exchange.FundingNotionalPriceType = model.FundingNotionalPriceType;

        _uow.Exchanges.Update(exchange);
        await _uow.SaveAsync();
        _uow.Exchanges.InvalidateCache();

        _logger.LogWarning("Admin {Action}: {EntityType} {EntityId} by {AdminUserId}",
            "Updated", "Exchange", exchange.Id, User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown");

        TempData["Success"] = $"Exchange '{exchange.Name}' updated successfully.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Delete(int id)
    {
        var exchange = await _uow.Exchanges.GetByIdAsync(id);
        if (exchange is null)
        {
            return NotFound();
        }

        return View(exchange);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var exchange = await _uow.Exchanges.GetByIdAsync(id);
        if (exchange is null)
        {
            return NotFound();
        }

        try
        {
            _uow.Exchanges.Remove(exchange);
            await _uow.SaveAsync();
            _uow.Exchanges.InvalidateCache();

            _logger.LogWarning("Admin {Action}: {EntityType} {EntityId} by {AdminUserId}",
                "Deleted", "Exchange", exchange.Id, User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown");
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            TempData["Error"] = "Cannot delete this exchange — it has associated positions or data.";
            return RedirectToAction(nameof(Delete), new { id });
        }

        TempData["Success"] = $"Exchange '{exchange.Name}' deleted successfully.";
        return RedirectToAction(nameof(Index));
    }

    private static IEnumerable<SelectListItem> GetFundingIntervalOptions() =>
        Enum.GetValues<FundingInterval>()
            .Select(fi => new SelectListItem { Text = fi.ToString(), Value = ((int)fi).ToString(System.Globalization.CultureInfo.InvariantCulture) });
}
