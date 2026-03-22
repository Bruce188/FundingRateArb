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

    /// <summary>
    /// Returns the maximum leverage supported by the exchange for the given asset.
    /// Returns null if max leverage cannot be determined (use configured leverage as fallback).
    /// </summary>
    Task<int?> GetMaxLeverageAsync(string asset, CancellationToken ct = default);

    /// <summary>
    /// Returns the next funding settlement time (UTC) for the given asset.
    /// Returns null if the settlement time cannot be determined.
    /// </summary>
    Task<DateTime?> GetNextFundingTimeAsync(string asset, CancellationToken ct = default);
}
