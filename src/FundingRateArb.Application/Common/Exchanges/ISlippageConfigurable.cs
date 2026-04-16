namespace FundingRateArb.Application.Common.Exchanges;

/// <summary>
/// Implemented by exchange connectors that support configurable slippage tolerance.
/// </summary>
public interface ISlippageConfigurable
{
    /// <summary>
    /// Configures adaptive slippage floor and cap for market orders.
    /// </summary>
    void ConfigureSlippage(decimal floor, decimal max);
}
