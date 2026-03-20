namespace FundingRateArb.Application.Common.Exchanges;

public interface IExchangeConnectorFactory
{
    IExchangeConnector GetConnector(string exchangeName);
    IEnumerable<IExchangeConnector> GetAllConnectors();

    /// <summary>
    /// Creates a short-lived connector instance with user-specific credentials.
    /// Returns null if the exchange name is unknown or credentials are insufficient.
    /// </summary>
    Task<IExchangeConnector?> CreateForUserAsync(
        string exchangeName,
        string? apiKey,
        string? apiSecret,
        string? walletAddress,
        string? privateKey);
}
