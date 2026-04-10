using System.Security.Claims;
using FundingRateArb.Application.Common.Interfaces;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FundingRateArb.Web.Areas.Admin.Controllers;

[Area("Admin")]
[Authorize(Roles = "Admin")]
public class ConnectivityTestController : Controller
{
    private readonly IConnectivityTestService _connectivityTestService;
    private readonly IUserSettingsService _userSettings;
    private readonly IUnitOfWork _uow;
    private readonly UserManager<ApplicationUser> _userManager;

    public ConnectivityTestController(
        IConnectivityTestService connectivityTestService,
        IUserSettingsService userSettings,
        IUnitOfWork uow,
        UserManager<ApplicationUser> userManager)
    {
        _connectivityTestService = connectivityTestService;
        _userSettings = userSettings;
        _uow = uow;
        _userManager = userManager;
    }

    public async Task<IActionResult> Index()
    {
        // Sequential DB lookups — EF Core DbContext is not thread-safe
        var users = await _userManager.Users
            .OrderBy(u => u.UserName)
            .Select(u => new { u.Id, u.UserName, u.Email })
            .Take(500)
            .ToListAsync();

        var allExchanges = await _uow.Exchanges.GetActiveAsync();
        var exchanges = allExchanges
            .Where(e => !e.IsDataOnly)
            .OrderBy(e => e.Name)
            .ToList();

        ViewBag.Users = users;
        ViewBag.UsersTruncated = users.Count >= 500;
        ViewBag.Exchanges = exchanges;

        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RunTest(string userId, int exchangeId, bool dryRun = true)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return BadRequest("userId is required");
        }

        if (exchangeId <= 0)
        {
            return BadRequest("Invalid exchangeId");
        }

        var adminUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(adminUserId))
        {
            return Unauthorized();
        }

        // Defense-in-depth: validate user exists at the controller level to return a clear
        // 400 before invoking the service. The service also handles missing data gracefully,
        // so this is an intentional extra round trip for better error responses.
        var targetUser = await _userManager.FindByIdAsync(userId);
        if (targetUser is null)
        {
            return BadRequest("User not found");
        }

        var result = await _connectivityTestService.RunTestAsync(
            adminUserId, userId, exchangeId, dryRun, HttpContext.RequestAborted);

        return Json(result);
    }

    [HttpGet]
    public async Task<IActionResult> GetUserExchanges(string userId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return Json(Array.Empty<int>());
        }

        // Defense-in-depth: validate user exists before querying credentials.
        // The service handles missing data gracefully, but this gives a clear 400.
        var targetUser = await _userManager.FindByIdAsync(userId);
        if (targetUser is null)
        {
            return BadRequest("User not found");
        }

        var credentials = await _userSettings.GetActiveCredentialsAsync(userId);
        var exchangeIds = credentials.Select(c => c.ExchangeId).ToList();

        return Json(exchangeIds);
    }
}
