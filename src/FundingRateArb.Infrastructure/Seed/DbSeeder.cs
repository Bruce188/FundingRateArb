using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FundingRateArb.Infrastructure.Seed;

public static class DbSeeder
{
    private const string AdminEmail = "admin@fundingratearb.com";

    public static async Task SeedAsync(IServiceProvider services)
    {
        var context = services.GetRequiredService<AppDbContext>();
        var roleMgr = services.GetRequiredService<RoleManager<IdentityRole>>();
        var userMgr = services.GetRequiredService<UserManager<ApplicationUser>>();

        var config = services.GetRequiredService<IConfiguration>();
        await SeedRolesAsync(roleMgr);
        await SeedAdminUserAsync(userMgr, config);
        await SeedExchangesAsync(context);
        await SeedAssetsAsync(context);
        await SeedBotConfigAsync(context, userMgr);
        await SeedAdminUserSettingsAsync(context, userMgr);
        await ReconcileAllUserPreferencesAsync(context);
    }

    private static async Task SeedRolesAsync(RoleManager<IdentityRole> roleMgr)
    {
        foreach (var role in new[] { "Admin", "Trader" })
        {
            if (!await roleMgr.RoleExistsAsync(role))
            {
                await roleMgr.CreateAsync(new IdentityRole(role));
            }
        }
    }

    private static async Task SeedAdminUserAsync(
        UserManager<ApplicationUser> userMgr, IConfiguration config)
    {
        var adminPassword = config["Seed:AdminPassword"]
            ?? Environment.GetEnvironmentVariable("SEED_ADMIN_PASSWORD")
            ?? throw new InvalidOperationException(
                "Admin seed password must be set via 'Seed:AdminPassword' in User Secrets " +
                "or the SEED_ADMIN_PASSWORD environment variable.");

        var existing = await userMgr.FindByEmailAsync(AdminEmail);
        if (existing is not null)
        {
            var token = await userMgr.GeneratePasswordResetTokenAsync(existing);
            await userMgr.ResetPasswordAsync(existing, token, adminPassword);
            return;
        }

        var admin = new ApplicationUser
        {
            UserName = AdminEmail,
            Email = AdminEmail,
            DisplayName = "Admin",
            EmailConfirmed = true,
        };
        var result = await userMgr.CreateAsync(admin, adminPassword);
        if (result.Succeeded)
        {
            await userMgr.AddToRoleAsync(admin, "Admin");
        }
    }

    private static async Task SeedExchangesAsync(AppDbContext context)
    {
        // Canonical list — entries appended here are auto-backfilled on next startup.
        var canonical = new[]
        {
            new Exchange
            {
                Name = "Hyperliquid",
                ApiBaseUrl = "https://api.hyperliquid.xyz",
                WsBaseUrl = "wss://api.hyperliquid.xyz/ws",
                FundingInterval = FundingInterval.Hourly,
                FundingIntervalHours = 1,
                FundingNotionalPriceType = FundingNotionalPriceType.OraclePrice,
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
                FundingRebateRate = 0.15m,
                SupportsSubAccounts = true,
                IsActive = true,
                Description = "Lighter DEX — API key + nonce, USDC collateral, zero fees"
            },
            new Exchange
            {
                Name = "Aster",
                ApiBaseUrl = "https://fapi.asterdex.com",
                WsBaseUrl = "wss://fstream.asterdex.com",
                FundingInterval = FundingInterval.EightHourly,
                FundingIntervalHours = 8,
                FundingSettlementType = FundingSettlementType.Periodic,
                FundingTimingDeviationSeconds = 15,
                SupportsSubAccounts = false,
                IsActive = true,
                Description = "Aster DEX — HMAC-SHA256, USDT collateral, 8-hour funding"
            },
            new Exchange
            {
                Name = "Binance",
                ApiBaseUrl = "https://fapi.binance.com",
                WsBaseUrl = "wss://fstream.binance.com",
                FundingInterval = FundingInterval.EightHourly,
                FundingIntervalHours = 8,
                FundingSettlementType = FundingSettlementType.Periodic,
                TakerFeeRate = 0.0005m,
                SupportsSubAccounts = false,
                IsActive = true,
                Description = "Binance Futures — HMAC-SHA256, USDT collateral, 8-hour funding"
            },
            new Exchange
            {
                Name = "dYdX",
                ApiBaseUrl = "https://indexer.dydx.trade/v4",
                WsBaseUrl = "",
                FundingInterval = FundingInterval.Hourly,
                FundingIntervalHours = 1,
                FundingSettlementType = FundingSettlementType.Continuous,
                FundingNotionalPriceType = FundingNotionalPriceType.OraclePrice,
                TakerFeeRate = 0.0003m,
                SupportsSubAccounts = true,
                IsActive = true,
                Description = "dYdX v4 — Cosmos appchain, USDC collateral, hourly funding"
            },
            new Exchange
            {
                Name = "CoinGlass",
                ApiBaseUrl = "https://open-api-v3.coinglass.com",
                WsBaseUrl = "",
                FundingInterval = FundingInterval.EightHourly,
                FundingIntervalHours = 8,
                SupportsSubAccounts = false,
                IsActive = true,
                IsDataOnly = true,
                Description = "CoinGlass aggregator — read-only funding rate data source. Not a trading venue."
            }
        };

        var existingNames = (await context.Exchanges.Select(e => e.Name).ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toAdd = canonical.Where(e => !existingNames.Contains(e.Name)).ToList();
        if (toAdd.Count == 0)
        {
            return;
        }

        context.Exchanges.AddRange(toAdd);
        await context.SaveChangesAsync();
    }

    private static async Task SeedAssetsAsync(AppDbContext context)
    {
        // Canonical list — entries appended here are auto-backfilled on next startup.
        var canonical = new (string Symbol, string Name)[]
        {
            ("BTC",       "Bitcoin"),
            ("ETH",       "Ethereum"),
            ("SOL",       "Solana"),
            ("ARB",       "Arbitrum"),
            ("AVAX",      "Avalanche"),
            ("0G",        "0G"),
            ("AAVE",      "Aave"),
            ("ADA",       "Cardano"),
            ("APT",       "Aptos"),
            ("ASTER",     "Aster"),
            ("AVNT",      "Aventus"),
            ("AXS",       "Axie Infinity"),
            ("AZTEC",     "Aztec"),
            ("BCH",       "Bitcoin Cash"),
            ("BERA",      "Berachain"),
            ("BNB",       "BNB"),
            ("CC",        "Cloudcoin"),
            ("CRV",       "Curve"),
            ("DASH",      "Dash"),
            ("DOGE",      "Dogecoin"),
            ("DOT",       "Polkadot"),
            ("DYDX",      "dYdX"),
            ("EIGEN",     "EigenLayer"),
            ("ENA",       "Ethena"),
            ("ETHFI",     "Ether.fi"),
            ("FARTCOIN",  "Fartcoin"),
            ("FIL",       "Filecoin"),
            ("FOGO",      "Fogo"),
            ("GRASS",     "Grass"),
            ("HBAR",      "Hedera"),
            ("HYPE",      "Hyperliquid"),
            ("ICP",       "Internet Computer"),
            ("IP",        "Story Protocol"),
            ("JTO",       "Jito"),
            ("JUP",       "Jupiter"),
            ("KAITO",     "Kaito"),
            ("LDO",       "Lido"),
            ("LINEA",     "Linea"),
            ("LINK",      "Chainlink"),
            ("LIT",       "Litentry"),
            ("LTC",       "Litecoin"),
            ("MEGA",      "MegaETH"),
            ("MET",       "Metis"),
            ("MNT",       "Mantle"),
            ("MON",       "Monad"),
            ("MORPHO",    "Morpho"),
            ("NEAR",      "Near Protocol"),
            ("ONDO",      "Ondo Finance"),
            ("OP",        "Optimism"),
            ("PAXG",      "Pax Gold"),
            ("PENDLE",    "Pendle"),
            ("PENGU",     "Pudgy Penguins"),
            ("POL",       "Polygon"),
            ("POPCAT",    "Popcat"),
            ("PROVE",     "Prove"),
            ("PUMP",      "PumpBTC"),
            ("PYTH",      "Pyth Network"),
            ("RESOLV",    "Resolv"),
            ("S",         "Sonic"),
            ("SEI",       "Sei"),
            ("SKR",       "Sakura"),
            ("SKY",       "Sky"),
            ("SPX",       "SPX6900"),
            ("STABLE",    "Stable"),
            ("STBL",      "Stablecomp"),
            ("STRK",      "Starknet"),
            ("SUI",       "Sui"),
            ("TAO",       "Bittensor"),
            ("TIA",       "Celestia"),
            ("TON",       "Toncoin"),
            ("TRUMP",     "Trump"),
            ("TRX",       "Tron"),
            ("UNI",       "Uniswap"),
            ("VIRTUAL",   "Virtuals Protocol"),
            ("VVV",       "Venice"),
            ("WIF",       "Dogwifhat"),
            ("WLD",       "Worldcoin"),
            ("WLFI",      "World Liberty Financial"),
            ("XLM",       "Stellar"),
            ("XMR",       "Monero"),
            ("XPL",       "XPL"),
            ("XRP",       "Ripple"),
            ("ZEC",       "Zcash"),
            ("ZK",        "ZKsync"),
            ("ZORA",      "Zora"),
            ("ZRO",       "LayerZero"),
        };

        var existingSymbols = (await context.Assets.Select(a => a.Symbol).ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toAdd = canonical
            .Where(a => !existingSymbols.Contains(a.Symbol))
            .Select(a => new Asset { Symbol = a.Symbol, Name = a.Name, IsActive = true })
            .ToList();
        if (toAdd.Count == 0)
        {
            return;
        }

        context.Assets.AddRange(toAdd);
        await context.SaveChangesAsync();
    }

    private static async Task SeedBotConfigAsync(AppDbContext context, UserManager<ApplicationUser> userMgr)
    {
        var admin = await userMgr.FindByEmailAsync(AdminEmail);
        if (admin is null)
        {
            return;
        }

        var existing = await context.BotConfigurations.FirstOrDefaultAsync();
        if (existing is not null)
        {
            // Backfill new fields if they have zero defaults from old migration
            if (existing.FeeAmortizationHours == 0)
            {
                existing.FeeAmortizationHours = 12;
                existing.MinPositionSizeUsdc = 5m;
                existing.MinVolume24hUsdc = 50_000m;
                existing.RateStalenessMinutes = 15;
                existing.DailyDrawdownPausePct = 0.08m;
                existing.ConsecutiveLossPause = 3;
                await context.SaveChangesAsync();
            }
            return;
        }

        context.BotConfigurations.Add(new BotConfiguration
        {
            IsEnabled = false,
            TotalCapitalUsdc = 39m,
            DefaultLeverage = 5,
            MaxConcurrentPositions = 1,
            OpenThreshold = 0.0002m,
            AlertThreshold = 0.0001m,
            CloseThreshold = -0.00005m,
            StopLossPct = 0.10m,
            MaxHoldTimeHours = 48,
            MinHoldTimeHours = 2,
            VolumeFraction = 0.001m,
            MaxCapitalPerPosition = 0.90m,
            BreakevenHoursMax = 8,
            AdaptiveHoldEnabled = true,
            FeeAmortizationHours = 12,
            MinPositionSizeUsdc = 5m,
            MinVolume24hUsdc = 50_000m,
            RateStalenessMinutes = 15,
            DailyDrawdownPausePct = 0.08m,
            ConsecutiveLossPause = 3,
            LastUpdatedAt = DateTime.UtcNow,
            UpdatedByUserId = admin.Id
        });
        await context.SaveChangesAsync();
    }

    private static async Task SeedAdminUserSettingsAsync(
        AppDbContext context, UserManager<ApplicationUser> userMgr)
    {
        var admin = await userMgr.FindByEmailAsync(AdminEmail);
        if (admin is null)
        {
            return;
        }

        // Create default UserConfiguration for admin if none exists
        var hasConfig = await context.UserConfigurations
            .AnyAsync(c => c.UserId == admin.Id);
        if (!hasConfig)
        {
            context.UserConfigurations.Add(new UserConfiguration
            {
                UserId = admin.Id,
                IsEnabled = false,
                OpenThreshold = 0.0002m,
                CloseThreshold = -0.00005m,
                AlertThreshold = 0.0001m,
                DefaultLeverage = 5,
                TotalCapitalUsdc = 39m,
                MaxCapitalPerPosition = 0.90m,
                MaxConcurrentPositions = 1,
                StopLossPct = 0.10m,
                MaxHoldTimeHours = 48,
                AllocationStrategy = AllocationStrategy.Concentrated,
                AllocationTopN = 3,
                FeeAmortizationHours = 12m,
                MinPositionSizeUsdc = 5m,
                MinVolume24hUsdc = 50_000m,
                RateStalenessMinutes = 15,
                DailyDrawdownPausePct = 0.08m,
                ConsecutiveLossPause = 3,
                CreatedAt = DateTime.UtcNow
            });
        }

        await context.SaveChangesAsync();
    }

    private static async Task ReconcileAllUserPreferencesAsync(AppDbContext context)
    {
        // For every existing user, ensure pref rows exist for every active exchange
        // and asset. Runs once per startup — new entries in the seed auto-propagate
        // to all users (not just the admin seeded at first launch).
        var activeExchangeIds = await context.Exchanges
            .Where(e => e.IsActive)
            .Select(e => e.Id)
            .ToListAsync();
        var activeAssetIds = await context.Assets
            .Where(a => a.IsActive)
            .Select(a => a.Id)
            .ToListAsync();

        if (activeExchangeIds.Count == 0 && activeAssetIds.Count == 0)
        {
            return;
        }

        var userIds = await context.Users.Select(u => u.Id).ToListAsync();
        if (userIds.Count == 0)
        {
            return;
        }

        var existingExchangePrefs = (await context.UserExchangePreferences
                .Select(p => new { p.UserId, p.ExchangeId })
                .ToListAsync())
            .Select(p => (p.UserId, p.ExchangeId))
            .ToHashSet();

        var existingAssetPrefs = (await context.UserAssetPreferences
                .Select(p => new { p.UserId, p.AssetId })
                .ToListAsync())
            .Select(p => (p.UserId, p.AssetId))
            .ToHashSet();

        var added = 0;
        foreach (var userId in userIds)
        {
            foreach (var exchangeId in activeExchangeIds)
            {
                if (existingExchangePrefs.Contains((userId, exchangeId)))
                {
                    continue;
                }

                context.UserExchangePreferences.Add(new UserExchangePreference
                {
                    UserId = userId,
                    ExchangeId = exchangeId,
                    IsEnabled = true
                });
                added++;
            }

            foreach (var assetId in activeAssetIds)
            {
                if (existingAssetPrefs.Contains((userId, assetId)))
                {
                    continue;
                }

                context.UserAssetPreferences.Add(new UserAssetPreference
                {
                    UserId = userId,
                    AssetId = assetId,
                    IsEnabled = true
                });
                added++;
            }
        }

        if (added > 0)
        {
            await context.SaveChangesAsync();
        }
    }
}
