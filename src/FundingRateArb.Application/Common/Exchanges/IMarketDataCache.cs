using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Application.Common.Exchanges;

public interface IMarketDataCache
{
    void Update(FundingRateDto rate);
    FundingRateDto? GetLatest(string exchangeName, string symbol);
    List<FundingRateDto> GetAllLatest();
    List<FundingRateDto> GetAllForExchange(string exchangeName);
    decimal GetMarkPrice(string exchangeName, string symbol);
    bool IsStale(string exchangeName, string symbol, TimeSpan maxAge);
    bool IsStaleForExchange(string exchangeName, TimeSpan maxAge);
}
