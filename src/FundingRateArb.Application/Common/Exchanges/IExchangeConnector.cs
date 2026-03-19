using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Common.Exchanges;

public interface IExchangeConnector
{
    string ExchangeName { get; }
    Task<List<FundingRateDto>> GetFundingRatesAsync(CancellationToken ct = default);
    Task<OrderResultDto> PlaceMarketOrderAsync(string asset, Side side, decimal sizeUsdc, int leverage, CancellationToken ct = default);
    Task<OrderResultDto> ClosePositionAsync(string asset, Side side, CancellationToken ct = default);
    Task<decimal> GetMarkPriceAsync(string asset, CancellationToken ct = default);
    Task<decimal> GetAvailableBalanceAsync(CancellationToken ct = default);
}
