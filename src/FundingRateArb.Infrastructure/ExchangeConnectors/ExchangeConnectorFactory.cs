using FundingRateArb.Application.Common.Exchanges;
using Microsoft.Extensions.DependencyInjection;

namespace FundingRateArb.Infrastructure.ExchangeConnectors;

public class ExchangeConnectorFactory : IExchangeConnectorFactory
{
    private readonly IServiceProvider _serviceProvider;

    private static readonly Dictionary<string, Type> ConnectorTypes = new()
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
}
