namespace FundingRateArb.Application.Common.Exchanges;

public interface IExchangeConnectorFactory
{
    IExchangeConnector GetConnector(string exchangeName);
    IEnumerable<IExchangeConnector> GetAllConnectors();
}
