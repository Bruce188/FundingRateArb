using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.Services;

/// <summary>
/// Dispatches per-exchange / per-symbol notional cap lookups to the relevant
/// connector. Currently only <see cref="AsterConnector"/> exposes a cap
/// (<c>MAX_NOTIONAL_VALUE</c>); other exchanges return <c>null</c> so the
/// SignalEngine treats them as "no cap known" and lets candidates pass.
///
/// Dependencies are accepted as nullable so the provider is registered even
/// when a single connector instance is not wired up (e.g. during unit tests or
/// on an environment where Aster is disabled).
/// </summary>
public class ExchangeSymbolConstraintsProvider : IExchangeSymbolConstraintsProvider
{
    private readonly AsterConnector? _aster;
    private readonly ILogger<ExchangeSymbolConstraintsProvider>? _logger;

    public ExchangeSymbolConstraintsProvider(
        AsterConnector? aster = null,
        ILogger<ExchangeSymbolConstraintsProvider>? logger = null)
    {
        _aster = aster;
        _logger = logger;
    }

    public async Task<decimal?> GetMaxNotionalAsync(string exchangeName, string symbol, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(exchangeName) || string.IsNullOrEmpty(symbol))
        {
            return null;
        }

        try
        {
            if (string.Equals(exchangeName, "Aster", StringComparison.OrdinalIgnoreCase) && _aster is not null)
            {
                var constraints = await _aster.GetSymbolConstraintsAsync(symbol, ct);
                return constraints?.MaxNotionalValue;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to resolve MAX_NOTIONAL_VALUE for {Exchange}/{Symbol}", exchangeName, symbol);
        }

        return null;
    }
}
