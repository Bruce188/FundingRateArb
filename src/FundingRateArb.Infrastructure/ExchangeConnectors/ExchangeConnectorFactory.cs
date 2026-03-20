using CryptoExchange.Net.Authentication;
using FundingRateArb.Application.Common.Exchanges;
using HyperLiquid.Net.Clients;
using Aster.Net.Clients;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly.Registry;

namespace FundingRateArb.Infrastructure.ExchangeConnectors;

public class ExchangeConnectorFactory : IExchangeConnectorFactory
{
    private readonly IServiceProvider _serviceProvider;

    private static readonly Dictionary<string, Type> ConnectorTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Hyperliquid", typeof(HyperliquidConnector) },
        { "Lighter",     typeof(LighterConnector) },
        { "Aster",       typeof(AsterConnector) }
    };

    public ExchangeConnectorFactory(IServiceProvider serviceProvider)
        => _serviceProvider = serviceProvider;

    public IExchangeConnector GetConnector(string exchangeName)
    {
        if (!ConnectorTypes.TryGetValue(exchangeName, out var type))
            throw new ArgumentException($"Unknown exchange: '{exchangeName}'. Valid values: {string.Join(", ", ConnectorTypes.Keys)}", nameof(exchangeName));

        return (IExchangeConnector)_serviceProvider.GetRequiredService(type);
    }

    public IEnumerable<IExchangeConnector> GetAllConnectors()
        => ConnectorTypes.Values.Select(t => (IExchangeConnector)_serviceProvider.GetRequiredService(t));

    public Task<IExchangeConnector?> CreateForUserAsync(
        string exchangeName,
        string? apiKey,
        string? apiSecret,
        string? walletAddress,
        string? privateKey)
    {
        IExchangeConnector? connector = exchangeName.ToLowerInvariant() switch
        {
            "hyperliquid" => CreateHyperliquidConnector(walletAddress, privateKey),
            "aster"       => CreateAsterConnector(apiKey, apiSecret),
            "lighter"     => CreateLighterConnector(walletAddress, privateKey, apiKey),
            _             => null
        };

        return Task.FromResult(connector);
    }

    public void ValidateRegistrations(IEnumerable<string> exchangeNames)
    {
        foreach (var name in exchangeNames)
        {
            try { GetConnector(name); }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    $"Exchange '{name}' in database has no registered connector. " +
                    "Check DI registration in Program.cs.", ex);
            }
        }
    }

    private HyperliquidConnector? CreateHyperliquidConnector(string? walletAddress, string? privateKey)
    {
        if (string.IsNullOrEmpty(walletAddress) || string.IsNullOrEmpty(privateKey))
            return null;

        var restClient = new HyperLiquidRestClient(options =>
        {
            options.ApiCredentials = new ApiCredentials(walletAddress, privateKey);
        });

        var pipelineProvider = _serviceProvider.GetRequiredService<ResiliencePipelineProvider<string>>();
        return new HyperliquidConnector(restClient, pipelineProvider);
    }

    private AsterConnector? CreateAsterConnector(string? apiKey, string? apiSecret)
    {
        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
            return null;

        var restClient = new AsterRestClient(options =>
        {
            options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
        });

        var pipelineProvider = _serviceProvider.GetRequiredService<ResiliencePipelineProvider<string>>();
        var logger = _serviceProvider.GetRequiredService<ILogger<AsterConnector>>();
        return new AsterConnector(restClient, pipelineProvider, logger);
    }

    private LighterConnector? CreateLighterConnector(
        string? walletAddress, string? privateKey, string? apiKeyIndex)
    {
        if (string.IsNullOrEmpty(privateKey))
            return null;

        // Build an in-memory configuration with user-specific Lighter credentials
        var configData = new Dictionary<string, string?>();
        if (!string.IsNullOrEmpty(privateKey))
            configData["Exchanges:Lighter:SignerPrivateKey"] = privateKey;
        if (!string.IsNullOrEmpty(apiKeyIndex))
            configData["Exchanges:Lighter:ApiKey"] = apiKeyIndex;
        if (!string.IsNullOrEmpty(walletAddress))
            configData["Exchanges:Lighter:AccountIndex"] = walletAddress;

        var userConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        // Create a new HttpClient with the Lighter base URL
        var httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://mainnet.zklighter.elliot.ai/api/v1/"),
            Timeout = TimeSpan.FromSeconds(30)
        };

        var logger = _serviceProvider.GetRequiredService<ILogger<LighterConnector>>();
        return new LighterConnector(httpClient, logger, userConfig);
    }
}
