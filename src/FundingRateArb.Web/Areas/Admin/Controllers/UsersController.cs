using System.Security.Claims;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Web.ViewModels.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace FundingRateArb.Web.Areas.Admin.Controllers;

[Area("Admin")]
[Authorize(Roles = "Admin")]
public class UsersController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly ILogger<UsersController> _logger;

    public UsersController(
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        ILogger<UsersController> logger)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _logger = logger;
    }

    public async Task<IActionResult> Index()
    {
        var users = await _userManager.Users.ToListAsync();

        var viewModels = new List<UserRoleViewModel>();
        foreach (var user in users)
        {
            var roles = await _userManager.GetRolesAsync(user);
            viewModels.Add(new UserRoleViewModel
            {
                UserId = user.Id,
                Email = user.Email ?? string.Empty,
                DisplayName = user.DisplayName ?? user.Email ?? user.Id,
                Roles = roles
            });
        }

        return View(viewModels);
    }

    public async Task<IActionResult> EditRole(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return NotFound();
        }

        var currentRoles = await _userManager.GetRolesAsync(user);
        var availableRoles = new[] { "Admin", "Trader" }
            .Select(r => new SelectListItem { Text = r, Value = r });

        var model = new UserRoleViewModel
        {
            UserId = user.Id,
            Email = user.Email ?? string.Empty,
            DisplayName = user.DisplayName ?? user.Email ?? user.Id,
            Roles = currentRoles,
            AvailableRoles = availableRoles
        };

        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EditRole(UserRoleViewModel model)
    {
        var user = await _userManager.FindByIdAsync(model.UserId);
        if (user is null)
        {
            return NotFound();
        }

        // Prevent admin from removing their own Admin role
        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var selectedRoles = model.Roles ?? [];
        if (model.UserId == currentUserId && !selectedRoles.Contains("Admin"))
        {
            TempData["Error"] = "Cannot remove your own Admin role.";
            return RedirectToAction(nameof(Index));
        }

        // Whitelist: only allow known application roles to prevent privilege escalation
        var validRoles = new HashSet<string> { "Admin", "Trader" };
        var filteredRoles = (model.Roles ?? new List<string>()).Where(r => validRoles.Contains(r)).ToList();

        // Remove all current roles and assign the selected ones
        var currentRoles = await _userManager.GetRolesAsync(user);
        await _userManager.RemoveFromRolesAsync(user, currentRoles);

        if (filteredRoles.Count > 0)
        {
            await _userManager.AddToRolesAsync(user, filteredRoles);
        }

        _logger.LogWarning("Admin {Action}: {EntityType} {EntityId} by {AdminUserId} — roles changed from [{OldRoles}] to [{NewRoles}]",
            "RoleChanged", "User", model.UserId, User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown",
            string.Join(", ", currentRoles), string.Join(", ", filteredRoles));

        TempData["Success"] = $"Role updated for {user.Email}.";
        return RedirectToAction(nameof(Index));
    }
}
