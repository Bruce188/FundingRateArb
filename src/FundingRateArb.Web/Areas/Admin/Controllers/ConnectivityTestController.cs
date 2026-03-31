using System.Security.Claims;
using FundingRateArb.Application.Common.Interfaces;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
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
    private readonly UserManager<IdentityUser> _userManager;

    public ConnectivityTestController(
        IConnectivityTestService connectivityTestService,
        IUserSettingsService userSettings,
        IUnitOfWork uow,
        UserManager<IdentityUser> userManager)
    {
        _connectivityTestService = connectivityTestService;
        _userSettings = userSettings;
        _uow = uow;
        _userManager = userManager;
    }

    public async Task<IActionResult> Index()
    {
        var users = await _userManager.Users
            .OrderBy(u => u.UserName)
            .Select(u => new { u.Id, u.UserName, u.Email })
            .Take(500)
            .ToListAsync();

        var exchanges = (await _uow.Exchanges.GetActiveAsync())
            .Where(e => !e.IsDataOnly)
            .OrderBy(e => e.Name)
            .ToList();

        ViewBag.Users = users;
        ViewBag.Exchanges = exchanges;

        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RunTest(string userId, int exchangeId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return BadRequest("userId is required");
        }

        var adminUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(adminUserId))
        {
            return Unauthorized();
        }

        // Validate that the target user actually exists
        var targetUser = await _userManager.FindByIdAsync(userId);
        if (targetUser is null)
        {
            return BadRequest("User not found");
        }

        var result = await _connectivityTestService.RunTestAsync(
            adminUserId, userId, exchangeId, HttpContext.RequestAborted);

        return Json(result);
    }

    [HttpGet]
    public async Task<IActionResult> GetUserExchanges(string userId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return Json(Array.Empty<int>());
        }

        // Validate that the target user actually exists
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
