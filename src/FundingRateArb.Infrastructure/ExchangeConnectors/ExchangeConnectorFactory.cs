using FundingRateArb.Application.Common.Exchanges;
using Microsoft.Extensions.DependencyInjection;

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
}
