using System.Text.Json.Serialization;

namespace FundingRateArb.Infrastructure.ExchangeConnectors.Models;

/// <summary>
/// Represents a single entry from the Lighter DEX /api/v1/funding-rates endpoint.
/// Each entry covers one symbol on one reference exchange (binance/bybit/hyperliquid/lighter).
/// </summary>
public class LighterFundingRateEntry
{
    [JsonPropertyName("market_id")]
    public int MarketId { get; set; }

    [JsonPropertyName("exchange")]
    public string Exchange { get; set; } = "";

    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = "";

    [JsonPropertyName("rate")]
    public decimal Rate { get; set; }
}

/// <summary>
/// Top-level response from the Lighter DEX /api/v1/funding-rates endpoint.
/// </summary>
public class LighterFundingRatesResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("funding_rates")]
    public List<LighterFundingRateEntry>? FundingRates { get; set; } = [];
}

/// <summary>
/// Single entry from the Lighter DEX /api/v1/exchangeStats endpoint.
/// </summary>
public class LighterOrderBookStat
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = "";

    [JsonPropertyName("daily_quote_token_volume")]
    public decimal DailyQuoteTokenVolume { get; set; }
}

/// <summary>
/// Top-level response from the Lighter DEX /api/v1/exchangeStats endpoint.
/// </summary>
public class LighterExchangeStatsResponse
{
    [JsonPropertyName("order_book_stats")]
    public List<LighterOrderBookStat>? OrderBookStats { get; set; } = [];
}

// ── Account API models (/api/v1/account) ──

/// <summary>
/// A single position within a Lighter account.
/// </summary>
public class LighterAccountPosition
{
    [JsonPropertyName("market_id")]
    public int MarketId { get; set; }

    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = "";

    /// <summary>1 = Long, -1 = Short.</summary>
    [JsonPropertyName("sign")]
    public int Sign { get; set; }

    [JsonPropertyName("position")]
    public string Position { get; set; } = "0";

    [JsonPropertyName("avg_entry_price")]
    public string AvgEntryPrice { get; set; } = "0";

    [JsonPropertyName("position_value")]
    public string PositionValue { get; set; } = "0";

    [JsonPropertyName("unrealized_pnl")]
    public string UnrealizedPnl { get; set; } = "0";

    [JsonPropertyName("realized_pnl")]
    public string RealizedPnl { get; set; } = "0";

    [JsonPropertyName("liquidation_price")]
    public string LiquidationPrice { get; set; } = "0";

    [JsonPropertyName("margin_mode")]
    public int MarginMode { get; set; }
}

/// <summary>
/// A single account entry from the Lighter account API.
/// </summary>
public class LighterAccount
{
    [JsonPropertyName("account_index")]
    public long AccountIndex { get; set; }

    [JsonPropertyName("available_balance")]
    public string AvailableBalance { get; set; } = "0";

    [JsonPropertyName("collateral")]
    public string Collateral { get; set; } = "0";

    [JsonPropertyName("total_asset_value")]
    public string TotalAssetValue { get; set; } = "0";

    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("positions")]
    public List<LighterAccountPosition>? Positions { get; set; }
}

/// <summary>
/// Top-level response from /api/v1/account.
/// </summary>
public class LighterAccountResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("accounts")]
    public List<LighterAccount>? Accounts { get; set; }
}

// ── Order Book Details models (/api/v1/orderBookDetails) ──

/// <summary>
/// A single market entry from /api/v1/orderBookDetails.
/// Provides tick/lot sizes, fees, margin fractions, and last trade price.
/// </summary>
public class LighterOrderBookDetail
{
    [JsonPropertyName("market_id")]
    public int MarketId { get; set; }

    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = "";

    [JsonPropertyName("taker_fee")]
    public string TakerFee { get; set; } = "0";

    [JsonPropertyName("maker_fee")]
    public string MakerFee { get; set; } = "0";

    [JsonPropertyName("min_base_amount")]
    public string MinBaseAmount { get; set; } = "0";

    [JsonPropertyName("supported_size_decimals")]
    public int SupportedSizeDecimals { get; set; }

    [JsonPropertyName("supported_price_decimals")]
    public int SupportedPriceDecimals { get; set; }

    [JsonPropertyName("size_decimals")]
    public int SizeDecimals { get; set; }

    [JsonPropertyName("price_decimals")]
    public int PriceDecimals { get; set; }

    [JsonPropertyName("last_trade_price")]
    public decimal LastTradePrice { get; set; }

    [JsonPropertyName("best_bid")]
    public decimal BestBid { get; set; }

    [JsonPropertyName("best_ask")]
    public decimal BestAsk { get; set; }

    [JsonPropertyName("default_initial_margin_fraction")]
    public int DefaultInitialMarginFraction { get; set; }

    [JsonPropertyName("min_initial_margin_fraction")]
    public int MinInitialMarginFraction { get; set; }

    [JsonPropertyName("maintenance_margin_fraction")]
    public int MaintenanceMarginFraction { get; set; }
}

/// <summary>
/// Top-level response from /api/v1/orderBookDetails.
/// </summary>
public class LighterOrderBookDetailsResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("order_book_details")]
    public List<LighterOrderBookDetail>? OrderBookDetails { get; set; }
}

// ── Asset Details models (/api/v1/assetDetails) ──

public class LighterAssetDetail
{
    [JsonPropertyName("asset_id")]
    public int AssetId { get; set; }

    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = "";

    [JsonPropertyName("index_price")]
    public string IndexPrice { get; set; } = "0";
}

public class LighterAssetDetailsResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("asset_details")]
    public List<LighterAssetDetail>? AssetDetails { get; set; }
}

// ── Nonce API model (/api/v1/nextNonce) ──

public class LighterNonceResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("nonce")]
    public int Nonce { get; set; }
}

// ── TX Status API model (/api/v1/tx) ──

/// <summary>
/// Response from GET /api/v1/tx?hash={txHash}.
/// Status codes: 0=Failed, 1=Pending, 2=Executed.
/// </summary>
public class LighterTxStatusResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("status")]
    public int Status { get; set; }
}

// ── Inactive Orders API model (/api/v1/accountInactiveOrders) ──

/// <summary>
/// A single inactive order from /api/v1/accountInactiveOrders.
/// </summary>
public class LighterInactiveOrder
{
    [JsonPropertyName("market_id")]
    public int MarketId { get; set; }

    [JsonPropertyName("cancel_reason")]
    public int CancelReason { get; set; }
}

/// <summary>
/// Response from GET /api/v1/accountInactiveOrders.
/// Cancellation codes: 8=Margin, 9=Slippage, 10=Liquidity, 16=Balance.
/// </summary>
public class LighterInactiveOrdersResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("inactive_orders")]
    public List<LighterInactiveOrder>? InactiveOrders { get; set; }
}

// ── Send Transaction API model (/api/v1/sendTx) ──

public class LighterSendTxResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("tx_hash")]
    public string? TxHash { get; set; }

    [JsonPropertyName("predicted_execution_time_ms")]
    public double PredictedExecutionTimeMs { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

// ── Position Funding API model (/api/v1/positionFunding) ──

/// <summary>
/// A single entry from /api/v1/positionFunding.
/// Amounts are signed from the account's perspective: negative = paid, positive = received.
/// </summary>
public class LighterPositionFundingEntry
{
    [JsonPropertyName("market_id")]
    public int MarketId { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("amount")]
    public string Amount { get; set; } = "0";

    [JsonPropertyName("rate")]
    public string Rate { get; set; } = "0";

    [JsonPropertyName("position_id")]
    public long PositionId { get; set; }
}

/// <summary>
/// Top-level response from /api/v1/positionFunding (cursor-paginated).
/// </summary>
public class LighterPositionFundingResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("position_funding")]
    public List<LighterPositionFundingEntry>? PositionFunding { get; set; } = [];

    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; set; }
}

// ── Trades API model (/api/v1/trades) ──

/// <summary>
/// A single trade entry from /api/v1/trades.
/// RealizedPnl is nullable because Lighter may omit it on non-PnL-realizing trades (e.g. opens).
/// </summary>
public class LighterTradeEntry
{
    [JsonPropertyName("trade_id")]
    public long TradeId { get; set; }

    [JsonPropertyName("market_id")]
    public int MarketId { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    /// <summary>0 = buy, 1 = sell.</summary>
    [JsonPropertyName("is_ask")]
    public int IsAsk { get; set; }

    [JsonPropertyName("size")]
    public string Size { get; set; } = "0";

    [JsonPropertyName("price")]
    public string Price { get; set; } = "0";

    [JsonPropertyName("quote_amount")]
    public string QuoteAmount { get; set; } = "0";

    [JsonPropertyName("fee")]
    public string Fee { get; set; } = "0";

    [JsonPropertyName("realized_pnl")]
    public string? RealizedPnl { get; set; }
}

/// <summary>
/// Top-level response from /api/v1/trades (cursor-paginated).
/// </summary>
public class LighterTradesResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("trades")]
    public List<LighterTradeEntry>? Trades { get; set; } = [];

    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; set; }
}
