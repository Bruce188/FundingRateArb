namespace FundingRateArb.Domain.Enums;

/// <summary>
/// Determines how an exchange applies funding payments to open positions.
/// Continuous: funding accrues approximately every hour (e.g. Hyperliquid, Lighter).
/// Periodic: funding is paid as a lump sum at fixed interval boundaries (e.g. Aster every 8 hours).
/// </summary>
public enum FundingSettlementType
{
    /// <summary>Funding accrues continuously (hourly settlement or sub-hourly).</summary>
    Continuous,

    /// <summary>Funding is paid at discrete interval boundaries (e.g. every 4h or 8h).</summary>
    Periodic
}
