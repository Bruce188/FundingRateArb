using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
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
    private readonly IExchangeConnectorFactory _connectorFactory;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(
        IUserSettingsService settings,
        IUnitOfWork uow,
        IExchangeConnectorFactory connectorFactory,
        ILogger<SettingsController> logger)
    {
        _settings = settings;
        _uow = uow;
        _connectorFactory = connectorFactory;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> ApiKeys()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Unauthorized();
        }

        var exchanges = await _settings.GetAvailableExchangesAsync();
        // Fetch all credentials in a single query to avoid N+1
        var allCredentials = await _settings.GetAllCredentialsAsync(userId);
        var credentialByExchange = allCredentials.ToDictionary(c => c.ExchangeId);
        var items = new List<ExchangeCredentialItem>();

        // Exclude data-only exchanges (e.g. CoinGlass) — they don't need credentials
        foreach (var exchange in exchanges.Where(e => !e.IsDataOnly))
        {
            credentialByExchange.TryGetValue(exchange.Id, out var credential);
            items.Add(BuildCredentialItem(exchange.Id, exchange.Name, credential));
        }

        var vm = new ApiKeyViewModel
        {
            Exchanges = items,
            TestResultMessage = TempData["TestResult"] as string,
            TestResultSuccess = TempData["TestResultSuccess"] as bool?,
        };

        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestApiKey(int exchangeId, CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Unauthorized();
        }

        var exchanges = await _settings.GetAvailableExchangesAsync();
        var exchange = exchanges.FirstOrDefault(e => e.Id == exchangeId);
        if (exchange is null)
        {
            return NotFound();
        }

        var credential = await _settings.GetCredentialAsync(userId, exchangeId);
        if (credential is null)
        {
            TempData["TestResult"] = "No credentials configured";
            TempData["TestResultSuccess"] = false;
            return RedirectToAction(nameof(ApiKeys));
        }

        var decrypted = _settings.DecryptCredential(credential);
        IExchangeConnector? connector = null;
        try
        {
            connector = await _connectorFactory.CreateForUserAsync(
                exchange.Name,
                decrypted.ApiKey,
                decrypted.ApiSecret,
                decrypted.WalletAddress,
                decrypted.PrivateKey,
                decrypted.SubAccountAddress,
                decrypted.ApiKeyIndex);

            if (connector is null)
            {
                TempData["TestResult"] = "Network error";
                TempData["TestResultSuccess"] = false;
                _logger.LogWarning("TestApiKey: factory returned null for exchange {ExchangeId}", exchangeId);
                return RedirectToAction(nameof(ApiKeys));
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var balance = await connector.GetAvailableBalanceAsync(cts.Token);
            TempData["TestResult"] = $"Connection OK — available balance: {balance:F2}";
            TempData["TestResultSuccess"] = true;
        }
        catch (OperationCanceledException)
        {
            TempData["TestResult"] = "Timeout";
            TempData["TestResultSuccess"] = false;
            _logger.LogWarning("TestApiKey timeout for exchange {ExchangeId}", exchangeId);
        }
        catch (Exception ex) when (IsUnauthorized(ex))
        {
            TempData["TestResult"] = "Unauthorized";
            TempData["TestResultSuccess"] = false;
            _logger.LogWarning(ex, "TestApiKey unauthorized for exchange {ExchangeId}", exchangeId);
        }
        catch (Exception ex) when (IsRateLimit(ex))
        {
            TempData["TestResult"] = "Exchange returned: 429";
            TempData["TestResultSuccess"] = false;
            _logger.LogWarning(ex, "TestApiKey rate-limited for exchange {ExchangeId}", exchangeId);
        }
        catch (Exception ex)
        {
            TempData["TestResult"] = "Network error";
            TempData["TestResultSuccess"] = false;
            _logger.LogWarning(ex, "TestApiKey failed for exchange {ExchangeId}", exchangeId);
        }
        finally
        {
            (connector as IDisposable)?.Dispose();
        }

        return RedirectToAction(nameof(ApiKeys));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveApiKey(
        int exchangeId,
        string? apiKey,
        string? apiSecret,
        string? walletAddress,
        string? privateKey,
        string? subAccountAddress,
        string? apiKeyIndex)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Unauthorized();
        }

        // Input validation: reject suspiciously long or whitespace-only values
        const int maxCredentialLength = 500;
        if (apiKey?.Length > maxCredentialLength || apiSecret?.Length > maxCredentialLength
            || walletAddress?.Length > maxCredentialLength || privateKey?.Length > maxCredentialLength
            || subAccountAddress?.Length > maxCredentialLength || apiKeyIndex?.Length > maxCredentialLength)
        {
            TempData["Error"] = "Credential value exceeds maximum allowed length.";
            return RedirectToAction(nameof(ApiKeys));
        }
        foreach (var (name, value) in new[] {
            ("API key", apiKey), ("API secret", apiSecret), ("Wallet address", walletAddress),
            ("Private key", privateKey), ("Sub-account address", subAccountAddress), ("API key index", apiKeyIndex) })
        {
            if (string.IsNullOrWhiteSpace(value) && !string.IsNullOrEmpty(value))
            {
                TempData["Error"] = $"{name} cannot be whitespace only.";
                return RedirectToAction(nameof(ApiKeys));
            }
        }

        // Per-exchange format validation
        if (!string.IsNullOrEmpty(subAccountAddress))
        {
            if (!Regex.IsMatch(subAccountAddress, @"^0x[0-9a-fA-F]{40}$"))
            {
                TempData["Error"] = "Sub-account address must be a valid Ethereum address (0x + 40 hex characters).";
                return RedirectToAction(nameof(ApiKeys));
            }
        }

        if (!string.IsNullOrEmpty(apiKeyIndex))
        {
            if (!int.TryParse(apiKeyIndex, out var idx) || idx < 2 || idx > 254)
            {
                TempData["Error"] = "API key index must be an integer between 2 and 254.";
                return RedirectToAction(nameof(ApiKeys));
            }
        }

        // Per-exchange validation: look up the exchange name by ID to determine the exchange type
        var exchanges = await _settings.GetAvailableExchangesAsync();
        var exchange = exchanges.FirstOrDefault(e => e.Id == exchangeId);
        var exchangeType = exchange is not null ? GetExchangeType(exchange.Name) : "cex";

        if (exchangeType == "dydx" && !string.IsNullOrWhiteSpace(privateKey))
        {
            var wordCount = privateKey.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (wordCount != 12 && wordCount != 24)
            {
                TempData["Error"] = "dYdX mnemonic must be exactly 12 or 24 words.";
                return RedirectToAction(nameof(ApiKeys));
            }
        }

        await _settings.SaveCredentialAsync(userId, exchangeId, apiKey, apiSecret, walletAddress, privateKey, subAccountAddress, apiKeyIndex);
        _logger.LogInformation("User {UserId} saved credentials for exchange {ExchangeId}", userId, exchangeId);

        // Validate credentials by attempting a balance fetch
        string? validationWarning = null;
        IExchangeConnector? validationConnector = null;
        try
        {
            var credential = await _settings.GetCredentialAsync(userId, exchangeId);
            if (credential is not null)
            {
                var decrypted = _settings.DecryptCredential(credential);
                validationConnector = await _connectorFactory.CreateForUserAsync(
                    exchange?.Name ?? "", decrypted.ApiKey, decrypted.ApiSecret,
                    decrypted.WalletAddress, decrypted.PrivateKey,
                    decrypted.SubAccountAddress, decrypted.ApiKeyIndex);

                if (validationConnector is not null)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await validationConnector.GetAvailableBalanceAsync(cts.Token);
                }
            }
        }
        catch (Exception ex)
        {
            validationWarning = "Credentials saved but validation failed. Check your API key and permissions.";
            _logger.LogWarning(ex, "Credential validation failed for exchange {ExchangeId} after save", exchangeId);
        }
        finally
        {
            (validationConnector as IDisposable)?.Dispose();
        }

        if (validationWarning is not null)
        {
            TempData["Error"] = validationWarning;
        }
        else
        {
            TempData["Success"] = "API key saved and validated successfully.";
        }

        return RedirectToAction(nameof(ApiKeys));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteApiKey(int exchangeId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Unauthorized();
        }

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
        var exchangeType = GetExchangeType(exchangeName);

        string? maskedApiKey = null;
        string? maskedWallet = null;
        string? maskedPrivateKey = null;
        string? maskedSubAccount = null;
        string? maskedApiKeyIndex = null;
        bool hasLegacyV1Credentials = false;

        if (credential is not null)
        {
            var decrypted = _settings.DecryptCredential(credential);
            maskedApiKey = MaskSecret(decrypted.ApiKey);
            maskedSubAccount = MaskSecret(decrypted.SubAccountAddress);
            maskedApiKeyIndex = MaskSecret(decrypted.ApiKeyIndex);

            // For Aster V3: WalletAddress stores a user private key — mask it entirely
            // to prevent even a partial reveal of the key in the UI.
            if (exchangeType == "aster-v3")
            {
                maskedWallet = MaskPrivateKey(decrypted.WalletAddress);
                maskedPrivateKey = MaskPrivateKey(decrypted.PrivateKey);

                // Legacy V1 detection: API key set but no V3 private keys configured.
                hasLegacyV1Credentials = !string.IsNullOrEmpty(decrypted.ApiKey)
                    && string.IsNullOrEmpty(decrypted.WalletAddress);
            }
            else if (exchangeType == "dydx")
            {
                // dYdX stores a BIP39 mnemonic in PrivateKey — mask it entirely.
                // Partial reveal would leak word-boundary information.
                maskedWallet = MaskSecret(decrypted.WalletAddress);
                maskedPrivateKey = MaskPrivateKey(decrypted.PrivateKey);
            }
            else
            {
                maskedWallet = MaskSecret(decrypted.WalletAddress);
                maskedPrivateKey = MaskSecret(decrypted.PrivateKey);
            }
        }

        return new ExchangeCredentialItem
        {
            ExchangeId = exchangeId,
            ExchangeName = exchangeName,
            IsConfigured = credential is not null && credential.IsActive,
            ExchangeType = exchangeType,
            MaskedApiKey = maskedApiKey,
            MaskedWalletAddress = maskedWallet,
            MaskedPrivateKey = maskedPrivateKey,
            MaskedSubAccountAddress = maskedSubAccount,
            MaskedApiKeyIndex = maskedApiKeyIndex,
            HasLegacyV1Credentials = hasLegacyV1Credentials,
            LastUsedAt = credential?.LastUsedAt,
            LastError = credential?.LastError,
            LastErrorAt = credential?.LastErrorAt,
        };
    }

    /// <summary>
    /// Returns the exchange type string for per-exchange form rendering.
    /// </summary>
    private static string GetExchangeType(string exchangeName) =>
        exchangeName.ToLowerInvariant() switch
        {
            "hyperliquid" => "hyperliquid",
            "lighter" => "lighter",
            "aster" => "aster-v3",
            "dydx" => "dydx",
            _ => "cex"
        };

    private static string? MaskSecret(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length < 10)
        {
            return "****";
        }

        return value[..4] + "..." + value[^4..];
    }

    /// <summary>
    /// Masks a private key entirely. Private keys must never be partially revealed in the UI,
    /// even as first-4/last-4 characters. Returns null for null input, "****" for any non-null value.
    /// </summary>
    private static string? MaskPrivateKey(string? value) =>
        value is null ? null : "****";

    private static bool IsUnauthorized(Exception ex) =>
        ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized }
        || ex.Message.Contains("401", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase);

    private static bool IsRateLimit(Exception ex) =>
        ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests }
        || ex.Message.Contains("429", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase);

    // -------------------------------------------------------------------------
    // Stream C — Exchange & Coin Preferences
    // -------------------------------------------------------------------------

    [HttpGet]
    public async Task<IActionResult> Preferences()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Unauthorized();
        }

        var exchanges = await _settings.GetAvailableExchangesAsync();
        var enabledExchangeIds = await _settings.GetUserEnabledExchangeIdsAsync(userId);
        var activeCredentials = await _settings.GetActiveCredentialsAsync(userId);
        var credentialExchangeIds = activeCredentials.Select(c => c.ExchangeId).ToHashSet();

        // Exclude data-only exchanges from trading preferences (e.g. CoinGlass)
        var exchangeItems = exchanges
            .Where(e => !e.IsDataOnly)
            .Select(e => new ExchangePreferenceItem
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
        if (userId is null)
        {
            return Unauthorized();
        }

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
        if (userId is null)
        {
            return Unauthorized();
        }

        var config = await _settings.GetOrCreateConfigAsync(userId);

        var globalConfig = await _uow.BotConfig.GetActiveAsync();

        // NB8: Guard against null — if no BotConfiguration exists, use an empty DTO with default values
        var adminDefaults = globalConfig is not null
            ? new DefaultConfigSummaryDto
            {
                TotalCapitalUsdc = globalConfig.TotalCapitalUsdc,
                DefaultLeverage = globalConfig.DefaultLeverage,
                MaxConcurrentPositions = globalConfig.MaxConcurrentPositions,
                MaxCapitalPerPosition = globalConfig.MaxCapitalPerPosition,
                OpenThreshold = globalConfig.OpenThreshold,
                CloseThreshold = globalConfig.CloseThreshold,
                AlertThreshold = globalConfig.AlertThreshold,
                StopLossPct = globalConfig.StopLossPct,
                MaxHoldTimeHours = globalConfig.MaxHoldTimeHours,
                DailyDrawdownPausePct = globalConfig.DailyDrawdownPausePct,
                ConsecutiveLossPause = globalConfig.ConsecutiveLossPause,
                MaxExposurePerAsset = globalConfig.MaxExposurePerAsset,
                MaxExposurePerExchange = globalConfig.MaxExposurePerExchange,
                AllocationStrategy = globalConfig.AllocationStrategy,
                AllocationTopN = globalConfig.AllocationTopN,
                FeeAmortizationHours = globalConfig.FeeAmortizationHours,
                MinPositionSizeUsdc = globalConfig.MinPositionSizeUsdc,
                MinVolume24hUsdc = globalConfig.MinVolume24hUsdc,
                RateStalenessMinutes = globalConfig.RateStalenessMinutes,
                FundingWindowMinutes = globalConfig.FundingWindowMinutes,
            }
            : new DefaultConfigSummaryDto();

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
            MaxExposurePerAsset = config.MaxExposurePerAsset,
            MaxExposurePerExchange = config.MaxExposurePerExchange,
            MaxLeverageCap = config.MaxLeverageCap,
            EmailNotificationsEnabled = config.EmailNotificationsEnabled,
            EmailCriticalAlerts = config.EmailCriticalAlerts,
            EmailDailySummary = config.EmailDailySummary,
            AllocationStrategyOptions = BuildAllocationStrategyOptions(config.AllocationStrategy),
            StatusMessage = TempData["Success"] as string,
            AdminDefaults = adminDefaults,
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
        if (userId is null)
        {
            return Unauthorized();
        }

        // Build candidate from viewmodel before touching the tracked entity,
        // matching the BotConfigController validate-before-mutate pattern.
        var candidateMaxLeverageCap = model.MaxLeverageCap;

        // Validate MaxLeverageCap against global cap before any entity mutation
        if (candidateMaxLeverageCap.HasValue)
        {
            var globalConfig = await _uow.BotConfig.GetActiveAsync();
            if (candidateMaxLeverageCap.Value > globalConfig.MaxLeverageCap)
            {
                ModelState.AddModelError(nameof(model.MaxLeverageCap),
                    $"Cannot exceed the global cap of {globalConfig.MaxLeverageCap}.");
                model.AllocationStrategyOptions = BuildAllocationStrategyOptions(model.AllocationStrategy);
                return View(model);
            }
        }

        // Validation passed — now mutate the tracked entity
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
        config.MaxExposurePerAsset = model.MaxExposurePerAsset!.Value;
        config.MaxExposurePerExchange = model.MaxExposurePerExchange!.Value;
        config.MaxLeverageCap = candidateMaxLeverageCap;
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
        if (userId is null)
        {
            return Unauthorized();
        }

        var globalConfig = await _uow.BotConfig.GetActiveAsync();
        if (globalConfig is null)
        {
            TempData["Error"] = "No global configuration found. Cannot reset to defaults.";
            return RedirectToAction(nameof(Configuration));
        }

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
        userConfig.MaxExposurePerAsset = globalConfig.MaxExposurePerAsset;
        userConfig.MaxExposurePerExchange = globalConfig.MaxExposurePerExchange;
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
                Value = ((int)s).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Text = s.ToString(),
                Selected = s == selected
            })
            .ToList();
}
