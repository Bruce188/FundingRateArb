namespace FundingRateArb.Application.Interfaces;

public interface IReferencePriceProvider
{
    decimal GetUnifiedPrice(string asset, string longExchange, string shortExchange);
}
