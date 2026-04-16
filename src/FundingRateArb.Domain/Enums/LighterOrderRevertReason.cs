namespace FundingRateArb.Domain.Enums;

public enum LighterOrderRevertReason
{
    None = 0,

    /// <summary>Lighter CancelReason 8 — account margin insufficient.</summary>
    MarginInsufficient = 8,

    /// <summary>Lighter CancelReason 9 — price slippage exceeded limit.</summary>
    Slippage = 9,

    /// <summary>Lighter CancelReason 10 — insufficient liquidity to fill order.</summary>
    LiquidityInsufficient = 10,

    /// <summary>Lighter CancelReason 16 — account balance insufficient.</summary>
    BalanceInsufficient = 16,

    /// <summary>Pre-submit liveness check: order book depth insufficient to fill order.</summary>
    InsufficientDepth = 100,

    /// <summary>All tx status polls exhausted — transaction still pending.</summary>
    Timeout = 101,

    /// <summary>Revert reason could not be mapped to a known code.</summary>
    Unknown = 999
}
