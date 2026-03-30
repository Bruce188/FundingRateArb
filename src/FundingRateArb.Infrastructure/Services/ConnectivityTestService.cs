using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Interfaces;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Hubs;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.Services;

public class ConnectivityTestService : IConnectivityTestService
{
    private readonly IUserSettingsService _userSettings;
    private readonly IExchangeConnectorFactory _connectorFactory;
    private readonly IUnitOfWork _uow;
    private readonly IHubContext<DashboardHub, IDashboardClient> _hub;
    private readonly ILogger<ConnectivityTestService> _logger;

    public ConnectivityTestService(
        IUserSettingsService userSettings,
        IExchangeConnectorFactory connectorFactory,
        IUnitOfWork uow,
        IHubContext<DashboardHub, IDashboardClient> hub,
        ILogger<ConnectivityTestService> logger)
    {
        _userSettings = userSettings;
        _connectorFactory = connectorFactory;
        _uow = uow;
        _hub = hub;
        _logger = logger;
    }

    public async Task<ConnectivityTestResult> RunTestAsync(
        string adminUserId, string targetUserId, int exchangeId, CancellationToken ct = default)
    {
        var exchange = await _uow.Exchanges.GetByIdAsync(exchangeId);
        if (exchange is null)
        {
            return new ConnectivityTestResult(false, "Unknown", "Exchange not found");
        }

        var exchangeName = exchange.Name;

        async Task Log(string msg)
        {
            _logger.LogInformation("[ConnectivityTest] [{Exchange}] {Message}", exchangeName, msg);
            await _hub.Clients.Group($"user-{adminUserId}")
                .ReceiveConnectivityLog(exchangeName, msg);
        }

        try
        {
            if (exchange.IsDataOnly)
            {
                await Log("Skipped: data-only exchange, no trading support");
                return new ConnectivityTestResult(false, exchangeName, "Data-only exchange, no trading support");
            }

            // Load and decrypt credentials
            var credential = await _uow.UserCredentials.GetByUserAndExchangeAsync(targetUserId, exchangeId);
            if (credential is null || !credential.IsActive)
            {
                await Log("No active credentials found for this user/exchange combination");
                return new ConnectivityTestResult(false, exchangeName, "No active credentials found");
            }

            await Log("Decrypting credentials...");
            var decrypted = _userSettings.DecryptCredential(credential);

            await Log("Creating exchange connector...");
            var connector = await _connectorFactory.CreateForUserAsync(
                exchangeName,
                decrypted.ApiKey,
                decrypted.ApiSecret,
                decrypted.WalletAddress,
                decrypted.PrivateKey,
                decrypted.SubAccountAddress,
                decrypted.ApiKeyIndex);

            if (connector is null)
            {
                await Log("Failed to create connector - invalid credentials");
                return new ConnectivityTestResult(false, exchangeName, "Failed to create connector - invalid credentials");
            }

            // Step 1 - Balance check
            await Log("Step 1: Checking available balance...");
            decimal balance;
            try
            {
                balance = await connector.GetAvailableBalanceAsync(ct);
                await Log($"Balance: ${balance:F2}");
            }
            catch (Exception ex)
            {
                await Log($"Balance check failed: {ex.Message}");
                return new ConnectivityTestResult(false, exchangeName, $"Balance check failed: {ex.Message}");
            }

            // Step 2 - Open position
            await Log("Step 2: Opening $5 ETH Long 1x position...");
            var openResult = await connector.PlaceMarketOrderAsync("ETH", Side.Long, 5m, 1, ct);
            if (!openResult.Success)
            {
                await Log($"Open failed: {openResult.Error}");
                return new ConnectivityTestResult(false, exchangeName, $"Open failed: {openResult.Error}", balance);
            }
            await Log($"Open SUCCESS - OrderId={openResult.OrderId} Price={openResult.FilledPrice} Qty={openResult.FilledQuantity}");

            // Step 3 - Wait for settlement
            await Log("Step 3: Waiting for settlement (2s)...");
            await Task.Delay(TimeSpan.FromSeconds(2), ct);

            // Step 4 - Close position
            await Log("Step 4: Closing position...");
            var closeResult = await connector.ClosePositionAsync("ETH", Side.Long, ct);
            if (!closeResult.Success)
            {
                await Log($"Close failed: {closeResult.Error}");
                return new ConnectivityTestResult(false, exchangeName, $"Close failed: {closeResult.Error}", balance);
            }
            await Log($"Close SUCCESS - OrderId={closeResult.OrderId}");

            await Log("PASS - All steps completed successfully");
            return new ConnectivityTestResult(true, exchangeName, null, balance);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connectivity test failed for {Exchange}", exchangeName);
            await Log($"Unexpected error: {ex.Message}");
            return new ConnectivityTestResult(false, exchangeName, $"Unexpected error: {ex.Message}");
        }
    }
}
