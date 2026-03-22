using System.Security.Claims;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace FundingRateArb.Web.Controllers;

[Authorize]
public class SettingsController : Controller
{
    private readonly IUserSettingsService _settings;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(IUserSettingsService settings, IUnitOfWork uow, ILogger<SettingsController> logger)
    {
        _settings = settings;
        _uow = uow;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> ApiKeys()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var exchanges = await _settings.GetAvailableExchangesAsync();
        // Fetch all credentials in a single query to avoid N+1
        var allCredentials = await _settings.GetAllCredentialsAsync(userId);
        var credentialByExchange = allCredentials.ToDictionary(c => c.ExchangeId);
        var items = new List<ExchangeCredentialItem>();

        foreach (var exchange in exchanges)
        {
            credentialByExchange.TryGetValue(exchange.Id, out var credential);
            items.Add(BuildCredentialItem(exchange.Id, exchange.Name, credential));
        }

        var vm = new ApiKeyViewModel
        {
            Exchanges = items
        };

        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveApiKey(
        int exchangeId,
        string? apiKey,
        string? apiSecret,
        string? walletAddress,
        string? privateKey)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        // Input validation: reject suspiciously long or whitespace-only values
        const int MaxCredentialLength = 500;
        if (apiKey?.Length > MaxCredentialLength || apiSecret?.Length > MaxCredentialLength
            || walletAddress?.Length > MaxCredentialLength || privateKey?.Length > MaxCredentialLength)
        {
            TempData["Error"] = "Credential value exceeds maximum allowed length.";
            return RedirectToAction(nameof(ApiKeys));
        }
        if (string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrEmpty(apiKey))
        {
            TempData["Error"] = "API key cannot be whitespace only.";
            return RedirectToAction(nameof(ApiKeys));
        }

        await _settings.SaveCredentialAsync(userId, exchangeId, apiKey, apiSecret, walletAddress, privateKey);
        TempData["Success"] = "API key saved successfully.";
        _logger.LogInformation("User {UserId} saved credentials for exchange {ExchangeId}", userId, exchangeId);

        return RedirectToAction(nameof(ApiKeys));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteApiKey(int exchangeId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        await _settings.DeleteCredentialAsync(userId, exchangeId);
        TempData["Success"] = "API key deleted successfully.";
        _logger.LogInformation("User {UserId} deleted credentials for exchange {ExchangeId}", userId, exchangeId);

        return RedirectToAction(nameof(ApiKeys));
    }

    // --- Helpers ---

    private ExchangeCredentialItem BuildCredentialItem(
        int exchangeId,
        string exchangeName,
        UserExchangeCredential? credential)
    {
        bool requiresWallet = IsWalletExchange(exchangeName);

        string? maskedApiKey = null;
        string? maskedWallet = null;

        if (credential is not null)
        {
            var (decryptedApiKey, _, decryptedWallet, _) = _settings.DecryptCredential(credential);
            maskedApiKey = MaskSecret(decryptedApiKey);
            maskedWallet = MaskSecret(decryptedWallet);
        }

        return new ExchangeCredentialItem
        {
            ExchangeId = exchangeId,
            ExchangeName = exchangeName,
            IsConfigured = credential is not null && credential.IsActive,
            RequiresWallet = requiresWallet,
            MaskedApiKey = maskedApiKey,
            MaskedWalletAddress = maskedWallet,
        };
    }

    /// <summary>
    /// Returns true for DEX-style exchanges that use wallet address + private key
    /// (Lighter, HyperLiquid). Returns false for CEX-style exchanges that use
    /// API key + API secret (Aster).
    /// </summary>
    private static bool IsWalletExchange(string exchangeName) =>
        exchangeName.Contains("Lighter", StringComparison.OrdinalIgnoreCase)
        || exchangeName.Contains("Hyperliquid", StringComparison.OrdinalIgnoreCase)
        || exchangeName.Contains("HyperLiquid", StringComparison.OrdinalIgnoreCase);

    private static string? MaskSecret(string? value)
    {
        if (value is null) return null;
        if (value.Length < 10) return "****";
        return value[..4] + "..." + value[^4..];
    }

    // -------------------------------------------------------------------------
    // Stream C — Exchange & Coin Preferences
    // -------------------------------------------------------------------------

    [HttpGet]
    public async Task<IActionResult> Preferences()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var exchanges = await _settings.GetAvailableExchangesAsync();
        var enabledExchangeIds = await _settings.GetUserEnabledExchangeIdsAsync(userId);
        var activeCredentials = await _settings.GetActiveCredentialsAsync(userId);
        var credentialExchangeIds = activeCredentials.Select(c => c.ExchangeId).ToHashSet();

        var exchangeItems = exchanges.Select(e => new ExchangePreferenceItem
        {
            ExchangeId = e.Id,
            ExchangeName = e.Name,
            IsEnabled = enabledExchangeIds.Contains(e.Id),
            HasCredentials = credentialExchangeIds.Contains(e.Id)
        }).ToList();

        var assets = await _settings.GetAvailableAssetsAsync();
        var enabledAssetIds = await _settings.GetUserEnabledAssetIdsAsync(userId);

        var assetItems = assets.Select(a => new AssetPreferenceItem
        {
            AssetId = a.Id,
            Symbol = a.Symbol,
            Name = a.Name,
            IsEnabled = enabledAssetIds.Contains(a.Id)
        }).ToList();

        var vm = new PreferencesViewModel
        {
            Exchanges = exchangeItems,
            Assets = assetItems,
            StatusMessage = TempData["Success"] as string,
            ErrorMessage = TempData["Error"] as string
        };

        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Preferences(
        List<int> enabledExchangeIds,
        List<int> enabledAssetIds)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        enabledExchangeIds ??= [];
        enabledAssetIds ??= [];

        if (enabledExchangeIds.Count < 2)
        {
            TempData["Error"] = "At least 2 exchanges must be enabled for arbitrage.";
            return RedirectToAction(nameof(Preferences));
        }

        if (enabledAssetIds.Count < 1)
        {
            TempData["Error"] = "At least 1 coin must be enabled to trade.";
            return RedirectToAction(nameof(Preferences));
        }

        // Build full dictionaries from available items so disabled ones are explicitly set to false
        var allExchanges = await _settings.GetAvailableExchangesAsync();
        var exchangePreferences = allExchanges.ToDictionary(e => e.Id, e => enabledExchangeIds.Contains(e.Id));

        var allAssets = await _settings.GetAvailableAssetsAsync();
        var assetPreferences = allAssets.ToDictionary(a => a.Id, a => enabledAssetIds.Contains(a.Id));

        await _settings.SavePreferencesAsync(userId, exchangePreferences, assetPreferences);

        _logger.LogInformation("User {UserId} saved preferences: {Exchanges} exchanges, {Assets} assets enabled",
            userId, enabledExchangeIds.Count, enabledAssetIds.Count);

        TempData["Success"] = "Preferences saved successfully.";
        return RedirectToAction(nameof(Preferences));
    }

    // -------------------------------------------------------------------------
    // Stream B — User Bot Configuration
    // -------------------------------------------------------------------------

    [HttpGet]
    public async Task<IActionResult> Configuration()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var config = await _settings.GetOrCreateConfigAsync(userId);

        var model = new UserConfigViewModel
        {
            IsEnabled = config.IsEnabled,
            OpenThreshold = config.OpenThreshold,
            CloseThreshold = config.CloseThreshold,
            AlertThreshold = config.AlertThreshold,
            DefaultLeverage = config.DefaultLeverage,
            TotalCapitalUsdc = config.TotalCapitalUsdc,
            MaxCapitalPerPosition = config.MaxCapitalPerPosition,
            MaxConcurrentPositions = config.MaxConcurrentPositions,
            StopLossPct = config.StopLossPct,
            MaxHoldTimeHours = config.MaxHoldTimeHours,
            AllocationStrategy = config.AllocationStrategy,
            AllocationTopN = config.AllocationTopN,
            FeeAmortizationHours = config.FeeAmortizationHours,
            MinPositionSizeUsdc = config.MinPositionSizeUsdc,
            MinVolume24hUsdc = config.MinVolume24hUsdc,
            RateStalenessMinutes = config.RateStalenessMinutes,
            DailyDrawdownPausePct = config.DailyDrawdownPausePct,
            ConsecutiveLossPause = config.ConsecutiveLossPause,
            FundingWindowMinutes = config.FundingWindowMinutes,
            EmailNotificationsEnabled = config.EmailNotificationsEnabled,
            EmailCriticalAlerts = config.EmailCriticalAlerts,
            EmailDailySummary = config.EmailDailySummary,
            AllocationStrategyOptions = BuildAllocationStrategyOptions(config.AllocationStrategy),
            StatusMessage = TempData["Success"] as string
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Configuration(UserConfigViewModel model)
    {
        if (!ModelState.IsValid)
        {
            model.AllocationStrategyOptions = BuildAllocationStrategyOptions(model.AllocationStrategy);
            return View(model);
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var config = await _settings.GetOrCreateConfigAsync(userId);

        config.IsEnabled = model.IsEnabled;
        config.OpenThreshold = model.OpenThreshold!.Value;
        config.CloseThreshold = model.CloseThreshold!.Value;
        config.AlertThreshold = model.AlertThreshold!.Value;
        config.DefaultLeverage = model.DefaultLeverage!.Value;
        config.TotalCapitalUsdc = model.TotalCapitalUsdc!.Value;
        config.MaxCapitalPerPosition = model.MaxCapitalPerPosition!.Value;
        config.MaxConcurrentPositions = model.MaxConcurrentPositions!.Value;
        config.StopLossPct = model.StopLossPct!.Value;
        config.MaxHoldTimeHours = model.MaxHoldTimeHours!.Value;
        config.AllocationStrategy = model.AllocationStrategy!.Value;
        config.AllocationTopN = model.AllocationTopN!.Value;
        config.FeeAmortizationHours = model.FeeAmortizationHours!.Value;
        config.MinPositionSizeUsdc = model.MinPositionSizeUsdc!.Value;
        config.MinVolume24hUsdc = model.MinVolume24hUsdc!.Value;
        config.RateStalenessMinutes = model.RateStalenessMinutes!.Value;
        config.DailyDrawdownPausePct = model.DailyDrawdownPausePct!.Value;
        config.ConsecutiveLossPause = model.ConsecutiveLossPause!.Value;
        config.FundingWindowMinutes = model.FundingWindowMinutes!.Value;
        config.EmailNotificationsEnabled = model.EmailNotificationsEnabled;
        config.EmailCriticalAlerts = model.EmailCriticalAlerts;
        config.EmailDailySummary = model.EmailDailySummary;
        config.LastUpdatedAt = DateTime.UtcNow;

        await _settings.UpdateConfigAsync(userId, config);

        _logger.LogInformation("User {UserId} updated their bot configuration", userId);
        TempData["Success"] = "Configuration saved successfully.";
        return RedirectToAction(nameof(Configuration));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetConfiguration()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var globalConfig = await _uow.BotConfig.GetActiveAsync();
        var userConfig = await _settings.GetOrCreateConfigAsync(userId);

        userConfig.OpenThreshold = globalConfig.OpenThreshold;
        userConfig.CloseThreshold = globalConfig.CloseThreshold;
        userConfig.AlertThreshold = globalConfig.AlertThreshold;
        userConfig.DefaultLeverage = globalConfig.DefaultLeverage;
        userConfig.TotalCapitalUsdc = globalConfig.TotalCapitalUsdc;
        userConfig.MaxCapitalPerPosition = globalConfig.MaxCapitalPerPosition;
        userConfig.MaxConcurrentPositions = globalConfig.MaxConcurrentPositions;
        userConfig.StopLossPct = globalConfig.StopLossPct;
        userConfig.MaxHoldTimeHours = globalConfig.MaxHoldTimeHours;
        userConfig.AllocationStrategy = globalConfig.AllocationStrategy;
        userConfig.AllocationTopN = globalConfig.AllocationTopN;
        userConfig.FeeAmortizationHours = globalConfig.FeeAmortizationHours;
        userConfig.MinPositionSizeUsdc = globalConfig.MinPositionSizeUsdc;
        userConfig.MinVolume24hUsdc = globalConfig.MinVolume24hUsdc;
        userConfig.RateStalenessMinutes = globalConfig.RateStalenessMinutes;
        userConfig.DailyDrawdownPausePct = globalConfig.DailyDrawdownPausePct;
        userConfig.ConsecutiveLossPause = globalConfig.ConsecutiveLossPause;
        userConfig.FundingWindowMinutes = globalConfig.FundingWindowMinutes;
        userConfig.LastUpdatedAt = DateTime.UtcNow;

        await _settings.UpdateConfigAsync(userId, userConfig);

        _logger.LogInformation("User {UserId} reset their bot configuration to global defaults", userId);
        TempData["Success"] = "Configuration reset to global defaults.";
        return RedirectToAction(nameof(Configuration));
    }

    private static List<SelectListItem> BuildAllocationStrategyOptions(AllocationStrategy? selected) =>
        Enum.GetValues<AllocationStrategy>()
            .Select(s => new SelectListItem
            {
                Value = ((int)s).ToString(),
                Text = s.ToString(),
                Selected = s == selected
            })
            .ToList();
}
