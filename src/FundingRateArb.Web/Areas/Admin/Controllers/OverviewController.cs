using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace FundingRateArb.Web.Areas.Admin.Controllers;

[Area("Admin")]
[Authorize(Roles = "Admin")]
public class OverviewController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IUnitOfWork _uow;
    private readonly IUserSettingsService _userSettings;

    public OverviewController(
        UserManager<ApplicationUser> userManager,
        IUnitOfWork uow,
        IUserSettingsService userSettings)
    {
        _userManager = userManager;
        _uow = uow;
        _userSettings = userSettings;
    }

    public async Task<IActionResult> Index()
    {
        var globalConfig = await _uow.BotConfig.GetActiveAsync();
        var allUsers = _userManager.Users.ToList();
        var allOpenPositions = await _uow.Positions.GetOpenAsync();

        var userItems = new List<UserSummaryItem>();

        foreach (var user in allUsers)
        {
            var userOpenPositions = allOpenPositions.Where(p => p.UserId == user.Id).ToList();
            var allUserPositions = await _uow.Positions.GetByUserAsync(user.Id);
            var userConfig = await _uow.UserConfigurations.GetByUserAsync(user.Id);
            var userCredentials = await _uow.UserCredentials.GetActiveByUserAsync(user.Id);

            var realizedPnl = allUserPositions
                .Where(p => p.RealizedPnl.HasValue)
                .Sum(p => p.RealizedPnl!.Value);

            var lastActivity = allUserPositions
                .OrderByDescending(p => p.ClosedAt ?? p.OpenedAt)
                .Select(p => (DateTime?)(p.ClosedAt ?? p.OpenedAt))
                .FirstOrDefault();

            userItems.Add(new UserSummaryItem
            {
                UserId = user.Id,
                Email = user.Email ?? user.UserName ?? user.Id,
                BotEnabled = userConfig?.IsEnabled ?? false,
                OpenPositions = userOpenPositions.Count,
                RealizedPnl = realizedPnl,
                ConfiguredExchanges = userCredentials.Count,
                LastActivity = lastActivity
            });
        }

        var slippageRollup = await _uow.Positions.GetSlippageRollupAsync(TimeSpan.FromDays(7));

        var model = new AdminOverviewViewModel
        {
            TotalActiveUsers = userItems.Count(u => u.BotEnabled),
            TotalOpenPositions = allOpenPositions.Count,
            AggregateRealizedPnl = userItems.Sum(u => u.RealizedPnl),
            AggregateUnrealizedPnl = allOpenPositions.Sum(p => p.AccumulatedFunding),
            GlobalBotEnabled = globalConfig.IsEnabled,
            Users = userItems,
            SlippageAttribution = slippageRollup,
        };

        return View(model);
    }
}
