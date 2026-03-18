using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Common.Exchanges;

public interface IExchangeConnector
{
    string ExchangeName { get; }
    Task<List<FundingRateDto>> GetFundingRatesAsync(CancellationToken ct = default);
    Task<OrderResultDto> PlaceMarketOrderAsync(string symbol, Side side, decimal quantity, CancellationToken ct = default);
    Task<OrderResultDto> ClosePositionAsync(string symbol, Side side, decimal quantity, string orderId, CancellationToken ct = default);
    Task<decimal> GetMarkPriceAsync(string symbol, CancellationToken ct = default);
    Task<decimal> GetAvailableBalanceAsync(CancellationToken ct = default);
}
