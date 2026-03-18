using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FundingRateArb.Infrastructure.Seed;

public static class DbSeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        var context = services.GetRequiredService<AppDbContext>();
        var roleMgr = services.GetRequiredService<RoleManager<IdentityRole>>();
        var userMgr = services.GetRequiredService<UserManager<ApplicationUser>>();

        await context.Database.MigrateAsync();

        await SeedRolesAsync(roleMgr);
        await SeedAdminUserAsync(userMgr);
        await SeedExchangesAsync(context);
        await SeedAssetsAsync(context);
        await SeedBotConfigAsync(context, userMgr);
    }

    private static async Task SeedRolesAsync(RoleManager<IdentityRole> roleMgr)
    {
        foreach (var role in new[] { "Admin", "Trader" })
        {
            if (!await roleMgr.RoleExistsAsync(role))
                await roleMgr.CreateAsync(new IdentityRole(role));
        }
    }

    private static async Task SeedAdminUserAsync(UserManager<ApplicationUser> userMgr)
    {
        const string adminEmail = "admin@fundingratearb.com";
        if (await userMgr.FindByEmailAsync(adminEmail) is null)
        {
            var admin = new ApplicationUser
            {
                UserName = adminEmail,
                Email = adminEmail,
                DisplayName = "Admin",
                EmailConfirmed = true
            };
            var result = await userMgr.CreateAsync(admin, "Admin123!");
            if (result.Succeeded)
                await userMgr.AddToRoleAsync(admin, "Admin");
        }
    }

    private static async Task SeedExchangesAsync(AppDbContext context)
    {
        if (await context.Exchanges.AnyAsync()) return;

        context.Exchanges.AddRange(
            new Exchange
            {
                Name = "Hyperliquid",
                ApiBaseUrl = "https://api.hyperliquid.xyz",
                WsBaseUrl = "wss://api.hyperliquid.xyz/ws",
                FundingInterval = FundingInterval.Hourly,
                FundingIntervalHours = 1,
                SupportsSubAccounts = true,
                IsActive = true,
                Description = "Hyperliquid DEX — EIP-712 wallet signing, USDC collateral"
            },
            new Exchange
            {
                Name = "Lighter",
                ApiBaseUrl = "https://mainnet.zklighter.elliot.ai/api/v1",
                WsBaseUrl = "wss://mainnet.zklighter.elliot.ai/stream",
                FundingInterval = FundingInterval.Hourly,
                FundingIntervalHours = 1,
                SupportsSubAccounts = true,
                IsActive = true,
                Description = "Lighter DEX — API key + nonce, USDC collateral, zero fees"
            },
            new Exchange
            {
                Name = "Aster",
                ApiBaseUrl = "https://fapi.asterdex.com",
                WsBaseUrl = "wss://fstream.asterdex.com",
                FundingInterval = FundingInterval.FourHourly,
                FundingIntervalHours = 4,
                SupportsSubAccounts = false,
                IsActive = true,
                Description = "Aster DEX — HMAC-SHA256, USDT collateral, 4-hour funding"
            }
        );
        await context.SaveChangesAsync();
    }

    private static async Task SeedAssetsAsync(AppDbContext context)
    {
        if (await context.Assets.AnyAsync()) return;

        context.Assets.AddRange(
            new Asset { Symbol = "BTC",  Name = "Bitcoin",   IsActive = true },
            new Asset { Symbol = "ETH",  Name = "Ethereum",  IsActive = true },
            new Asset { Symbol = "SOL",  Name = "Solana",    IsActive = true },
            new Asset { Symbol = "ARB",  Name = "Arbitrum",  IsActive = true },
            new Asset { Symbol = "AVAX", Name = "Avalanche", IsActive = true }
        );
        await context.SaveChangesAsync();
    }

    private static async Task SeedBotConfigAsync(AppDbContext context, UserManager<ApplicationUser> userMgr)
    {
        if (await context.BotConfigurations.AnyAsync()) return;

        var admin = await userMgr.FindByEmailAsync("admin@fundingratearb.com");
        if (admin is null) return;

        context.BotConfigurations.Add(new BotConfiguration
        {
            IsEnabled = false,
            TotalCapitalUsdc = 107m,
            DefaultLeverage = 5,
            MaxConcurrentPositions = 1,
            OpenThreshold = 0.0003m,
            AlertThreshold = 0.0001m,
            CloseThreshold = -0.00005m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            VolumeFraction = 0.001m,
            MaxCapitalPerPosition = 0.80m,
            BreakevenHoursMax = 6,
            LastUpdatedAt = DateTime.UtcNow,
            UpdatedByUserId = admin.Id
        });
        await context.SaveChangesAsync();
    }
}
