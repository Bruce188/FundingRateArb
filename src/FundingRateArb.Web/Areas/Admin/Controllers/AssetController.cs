using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Web.ViewModels.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FundingRateArb.Web.Areas.Admin.Controllers;

[Area("Admin")]
[Authorize(Roles = "Admin")]
public class AssetController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly ILogger<AssetController> _logger;

    public AssetController(IUnitOfWork uow, ILogger<AssetController> logger)
    {
        _uow = uow;
        _logger = logger;
    }

    public async Task<IActionResult> Index()
    {
        var assets = await _uow.Assets.GetAllAsync();
        return View(assets);
    }

    public IActionResult Create()
    {
        return View(new AssetCreateViewModel());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(AssetCreateViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        var asset = new Asset
        {
            Symbol = model.Symbol.ToUpperInvariant(),
            Name = model.Name,
            IsActive = model.IsActive
        };

        _uow.Assets.Add(asset);
        await _uow.SaveAsync();
        _uow.Assets.InvalidateCache();

        _logger.LogInformation("Admin {Action}: {EntityType} {EntityId} by {AdminUser}",
            "Created", "Asset", asset.Id, User.Identity?.Name ?? "unknown");

        TempData["Success"] = $"Asset '{asset.Symbol}' created successfully.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id)
    {
        var asset = await _uow.Assets.GetByIdAsync(id);
        if (asset is null)
            return NotFound();

        var model = new AssetEditViewModel
        {
            Id = asset.Id,
            Symbol = asset.Symbol,
            Name = asset.Name,
            IsActive = asset.IsActive
        };

        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, AssetEditViewModel model)
    {
        if (id != model.Id)
            return BadRequest();

        if (!ModelState.IsValid)
            return View(model);

        var asset = await _uow.Assets.GetByIdAsync(id);
        if (asset is null)
            return NotFound();

        asset.Symbol = model.Symbol.ToUpperInvariant();
        asset.Name = model.Name;
        asset.IsActive = model.IsActive;

        _uow.Assets.Update(asset);
        await _uow.SaveAsync();
        _uow.Assets.InvalidateCache();

        _logger.LogInformation("Admin {Action}: {EntityType} {EntityId} by {AdminUser}",
            "Updated", "Asset", asset.Id, User.Identity?.Name ?? "unknown");

        TempData["Success"] = $"Asset '{asset.Symbol}' updated successfully.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Delete(int id)
    {
        var asset = await _uow.Assets.GetByIdAsync(id);
        if (asset is null)
            return NotFound();

        return View(asset);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var asset = await _uow.Assets.GetByIdAsync(id);
        if (asset is null)
            return NotFound();

        try
        {
            _uow.Assets.Remove(asset);
            await _uow.SaveAsync();
            _uow.Assets.InvalidateCache();

            _logger.LogInformation("Admin {Action}: {EntityType} {EntityId} by {AdminUser}",
                "Deleted", "Asset", asset.Id, User.Identity?.Name ?? "unknown");
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            TempData["Error"] = "Cannot delete this asset — it has associated positions or data.";
            return RedirectToAction(nameof(Delete), new { id });
        }

        TempData["Success"] = $"Asset '{asset.Symbol}' deleted successfully.";
        return RedirectToAction(nameof(Index));
    }
}
