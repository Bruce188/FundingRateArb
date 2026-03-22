using System.Security.Claims;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace FundingRateArb.Web.Controllers;

public class AccountController : Controller
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IUserSettingsService _userSettings;
    private readonly ILogger<AccountController> _logger;

    public AccountController(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        IUserSettingsService userSettings,
        ILogger<AccountController> logger)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _userSettings = userSettings;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult ExternalLogin(string provider, string? returnUrl = null)
    {
        var redirectUrl = Url.Action(nameof(ExternalLoginCallback), "Account", new { returnUrl });
        var properties = _signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
        return Challenge(properties, provider);
    }

    [HttpGet]
    public async Task<IActionResult> ExternalLoginCallback(string? returnUrl = null, string? remoteError = null)
    {
        returnUrl ??= Url.Content("~/");

        if (remoteError is not null)
        {
            TempData["Error"] = $"External login failed: {System.Net.WebUtility.HtmlEncode(remoteError)}";
            return Redirect("/Identity/Account/Login");
        }

        var info = await _signInManager.GetExternalLoginInfoAsync();
        if (info is null)
        {
            TempData["Error"] = "Could not retrieve external login information.";
            return Redirect("/Identity/Account/Login");
        }

        // Try to sign in with the external login
        var result = await _signInManager.ExternalLoginSignInAsync(
            info.LoginProvider, info.ProviderKey, isPersistent: false, bypassTwoFactor: true);

        if (result.Succeeded)
        {
            _logger.LogInformation("User signed in via {Provider}", info.LoginProvider);
            return LocalRedirect(returnUrl);
        }

        if (result.IsLockedOut)
        {
            TempData["Error"] = "Account is locked out. Please try again later.";
            return Redirect("/Identity/Account/Login");
        }

        // User doesn't have an account yet — auto-register
        var email = info.Principal.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrEmpty(email))
        {
            TempData["Error"] = "External login did not provide an email address.";
            return Redirect("/Identity/Account/Login");
        }

        // Check if a user with this email already exists
        var existingUser = await _userManager.FindByEmailAsync(email);
        if (existingUser is not null)
        {
            // Link the external login to the existing user
            var addResult = await _userManager.AddLoginAsync(existingUser, info);
            if (addResult.Succeeded)
            {
                await _signInManager.SignInAsync(existingUser, isPersistent: false);
                _logger.LogInformation("Linked {Provider} login to existing user {Email}", info.LoginProvider, email);
                return LocalRedirect(returnUrl);
            }

            TempData["Error"] = "Failed to link external login to existing account.";
            return Redirect("/Identity/Account/Login");
        }

        // Create new user
        var displayName = info.Principal.FindFirstValue(ClaimTypes.Name) ?? email.Split('@')[0];
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true, // External providers verify email
            DisplayName = displayName,
        };

        var createResult = await _userManager.CreateAsync(user);
        if (!createResult.Succeeded)
        {
            TempData["Error"] = "Failed to create account: " +
                string.Join(", ", createResult.Errors.Select(e => e.Description));
            return Redirect("/Identity/Account/Login");
        }

        var loginResult = await _userManager.AddLoginAsync(user, info);
        if (!loginResult.Succeeded)
        {
            _logger.LogWarning("Failed to add external login for new user {Email}", email);
        }

        // Assign default role
        await _userManager.AddToRoleAsync(user, "Trader");

        // Initialize default preferences for the new user
        await _userSettings.InitializeDefaultsForNewUserAsync(user.Id);

        await _signInManager.SignInAsync(user, isPersistent: false);
        _logger.LogInformation("Created new user {Email} via {Provider}", email, info.LoginProvider);

        return LocalRedirect(returnUrl);
    }
}
