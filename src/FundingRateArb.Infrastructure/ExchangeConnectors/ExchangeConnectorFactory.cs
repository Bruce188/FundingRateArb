using System.Collections.Concurrent;
using Aster.Net.Clients;
using CryptoExchange.Net.Authentication;
using FundingRateArb.Application.Common.Exchanges;
using HyperLiquid.Net.Clients;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly.Registry;

namespace FundingRateArb.Infrastructure.ExchangeConnectors;

public class ExchangeConnectorFactory : IExchangeConnectorFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ExchangeConnectorFactory> _logger;
    private readonly ConcurrentDictionary<string, KeyPool> _keyPools = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, Type> ConnectorTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Hyperliquid", typeof(HyperliquidConnector) },
        { "Lighter",     typeof(LighterConnector) },
        { "Aster",       typeof(AsterConnector) },
        { "CoinGlass",   typeof(CoinGlassConnector) }
    };

    public ExchangeConnectorFactory(IServiceProvider serviceProvider, ILogger<ExchangeConnectorFactory> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Registers additional infrastructure connectors for an exchange, enabling round-robin key rotation.
    /// </summary>
    public void RegisterInfraConnectors(string exchangeName, IReadOnlyList<IExchangeConnector> connectors)
    {
        if (connectors.Count == 0)
        {
            return;
        }

        _keyPools[exchangeName] = new KeyPool(connectors);
    }

    public IExchangeConnector GetConnector(string exchangeName)
    {
        // If a key pool exists, use round-robin; otherwise fall back to DI singleton
        if (_keyPools.TryGetValue(exchangeName, out var pool))
        {
            var connector = pool.GetNext();
            if (connector is not null)
            {
                return connector;
            }
            // All keys in cooldown — fall through to DI singleton
        }

        if (!ConnectorTypes.TryGetValue(exchangeName, out var type))
        {
            throw new ArgumentException($"Unknown exchange: '{exchangeName}'. Valid values: {string.Join(", ", ConnectorTypes.Keys)}", nameof(exchangeName));
        }

        return (IExchangeConnector)_serviceProvider.GetRequiredService(type);
    }

    public IEnumerable<IExchangeConnector> GetAllConnectors()
    {
        foreach (var kvp in ConnectorTypes)
        {
            if (_keyPools.TryGetValue(kvp.Key, out var pool))
            {
                var connector = pool.GetNext();
                if (connector is not null)
                {
                    yield return connector;
                    continue;
                }
            }
            yield return (IExchangeConnector)_serviceProvider.GetRequiredService(kvp.Value);
        }
    }

    /// <summary>
    /// Marks a key as rate-limited. The connector will be skipped for the cooldown duration.
    /// </summary>
    public void MarkRateLimited(string exchangeName, IExchangeConnector connector, TimeSpan? cooldown = null)
    {
        if (_keyPools.TryGetValue(exchangeName, out var pool))
        {
            pool.MarkCooldown(connector, cooldown ?? TimeSpan.FromSeconds(60));
        }
    }

    /// <summary>Exposes key pool internals for testing.</summary>
    internal KeyPool? GetKeyPool(string exchangeName) =>
        _keyPools.TryGetValue(exchangeName, out var pool) ? pool : null;

    public Task<IExchangeConnector?> CreateForUserAsync(
        string exchangeName,
        string? apiKey,
        string? apiSecret,
        string? walletAddress,
        string? privateKey,
        string? subAccountAddress = null,
        string? apiKeyIndex = null)
    {
        IExchangeConnector? connector = exchangeName.ToLowerInvariant() switch
        {
            "hyperliquid" => CreateHyperliquidConnector(walletAddress, privateKey, subAccountAddress),
            "aster" => CreateAsterConnector(apiKey, apiSecret),
            "lighter" => CreateLighterConnector(walletAddress, privateKey, apiKeyIndex),
            "coinglass" => throw new NotSupportedException("CoinGlass is a read-only data source and cannot be used for trading"),
            _ => null
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

    private HyperliquidConnector? CreateHyperliquidConnector(string? walletAddress, string? privateKey, string? subAccountAddress)
    {
        if (string.IsNullOrEmpty(walletAddress) || string.IsNullOrEmpty(privateKey))
        {
            return null;
        }

        var restClient = new HyperLiquidRestClient(options =>
        {
            options.ApiCredentials = new ApiCredentials(walletAddress, privateKey);
        });

        var pipelineProvider = _serviceProvider.GetRequiredService<ResiliencePipelineProvider<string>>();
        return new HyperliquidConnector(restClient, pipelineProvider, subAccountAddress);
    }

    private AsterConnector? CreateAsterConnector(string? apiKey, string? apiSecret)
    {
        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
        {
            return null;
        }

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
        {
            return null;
        }

        // Build an in-memory configuration with user-specific Lighter credentials
        var configData = new Dictionary<string, string?>();
        if (!string.IsNullOrEmpty(privateKey))
        {
            configData["Exchanges:Lighter:SignerPrivateKey"] = privateKey;
        }

        if (!string.IsNullOrEmpty(apiKeyIndex))
        {
            configData["Exchanges:Lighter:ApiKey"] = apiKeyIndex;
        }

        if (!string.IsNullOrEmpty(walletAddress))
        {
            // Lighter expects a numeric account index, not a hex wallet address
            if (walletAddress.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                || !long.TryParse(walletAddress, out _))
            {
                var masked = MaskValue(walletAddress);
                _logger.LogWarning(
                    "Lighter Account Index must be numeric but received '{MaskedValue}'. " +
                    "Update your API key settings with a numeric account index",
                    masked);
                return null;
            }

            configData["Exchanges:Lighter:AccountIndex"] = walletAddress;
        }

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

    /// <summary>
    /// Masks a credential value for safe logging. Fully masks short values;
    /// shows first 4 and last 4 characters only for values longer than 12 characters.
    /// </summary>
    private static string MaskValue(string value)
    {
        if (value.Length <= 12)
        {
            return new string('*', value.Length);
        }

        return string.Concat(value.AsSpan(0, 4), "****", value.AsSpan(value.Length - 4));
    }

    /// <summary>
    /// Manages a pool of connectors for a single exchange with round-robin rotation and cooldown tracking.
    /// </summary>
    internal class KeyPool
    {
        private readonly IReadOnlyList<IExchangeConnector> _connectors;
        private readonly ConcurrentDictionary<IExchangeConnector, DateTime> _cooldowns = new();
        private int _index = -1;

        public KeyPool(IReadOnlyList<IExchangeConnector> connectors)
        {
            _connectors = connectors;
        }

        /// <summary>
        /// Returns the next non-cooled-down connector in round-robin order.
        /// If all connectors are cooled down, returns the one with the shortest remaining cooldown.
        /// Returns null only if the pool is empty.
        /// </summary>
        public IExchangeConnector? GetNext()
        {
            if (_connectors.Count == 0)
            {
                return null;
            }

            var now = DateTime.UtcNow;

            // Try round-robin: find next non-cooled-down connector
            for (int i = 0; i < _connectors.Count; i++)
            {
                var idx = (Interlocked.Increment(ref _index) & 0x7FFFFFFF) % _connectors.Count;
                var connector = _connectors[idx];

                if (!_cooldowns.TryGetValue(connector, out var expiresAt) || now >= expiresAt)
                {
                    // Clean up expired cooldown
                    _cooldowns.TryRemove(connector, out _);
                    return connector;
                }
            }

            // All in cooldown — return the one expiring soonest
            return _connectors
                .OrderBy(c => _cooldowns.GetValueOrDefault(c, DateTime.MinValue))
                .First();
        }

        public void MarkCooldown(IExchangeConnector connector, TimeSpan duration)
        {
            _cooldowns[connector] = DateTime.UtcNow + duration;
        }

        /// <summary>Expose for testing.</summary>
        internal int Count => _connectors.Count;
        internal bool IsInCooldown(IExchangeConnector connector) =>
            _cooldowns.TryGetValue(connector, out var exp) && DateTime.UtcNow < exp;
    }
}
