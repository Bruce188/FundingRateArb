using System.Text.Json;
using System.Text.Json.Serialization;

namespace FundingRateArb.Infrastructure.ExchangeConnectors.Dydx;

// DTOs for dYdX v4 Indexer REST API responses.
// All numeric values are strings in JSON — use custom converters or manual parsing.

public sealed class DydxPerpetualMarketsResponse
{
    [JsonPropertyName("markets")]
    public Dictionary<string, DydxPerpetualMarket> Markets { get; set; } = new();
}

public sealed class DydxPerpetualMarket
{
    [JsonPropertyName("ticker")]
    public string Ticker { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("oraclePrice")]
    [JsonConverter(typeof(StringDecimalConverter))]
    public decimal OraclePrice { get; set; }

    [JsonPropertyName("atomicResolution")]
    public int AtomicResolution { get; set; }

    [JsonPropertyName("quantumConversionExponent")]
    public int QuantumConversionExponent { get; set; }

    [JsonPropertyName("stepSize")]
    [JsonConverter(typeof(StringDecimalConverter))]
    public decimal StepSize { get; set; }

    [JsonPropertyName("tickSize")]
    [JsonConverter(typeof(StringDecimalConverter))]
    public decimal TickSize { get; set; }

    [JsonPropertyName("initialMarginFraction")]
    [JsonConverter(typeof(StringDecimalConverter))]
    public decimal InitialMarginFraction { get; set; }

    [JsonPropertyName("stepBaseQuantums")]
    public long StepBaseQuantums { get; set; }

    [JsonPropertyName("subticksPerTick")]
    public long SubticksPerTick { get; set; }

    [JsonPropertyName("clobPairId")]
    [JsonConverter(typeof(StringInt32Converter))]
    public int ClobPairId { get; set; }

    [JsonPropertyName("nextFundingRate")]
    [JsonConverter(typeof(StringNullableDecimalConverter))]
    public decimal? NextFundingRate { get; set; }
}

public sealed class DydxHistoricalFundingResponse
{
    [JsonPropertyName("historicalFunding")]
    public List<DydxHistoricalFunding> HistoricalFunding { get; set; } = [];
}

public sealed class DydxHistoricalFunding
{
    [JsonPropertyName("ticker")]
    public string Ticker { get; set; } = string.Empty;

    [JsonPropertyName("rate")]
    [JsonConverter(typeof(StringDecimalConverter))]
    public decimal Rate { get; set; }

    [JsonPropertyName("price")]
    [JsonConverter(typeof(StringDecimalConverter))]
    public decimal Price { get; set; }

    [JsonPropertyName("effectiveAt")]
    public DateTime EffectiveAt { get; set; }
}

public sealed class DydxSubaccountResponse
{
    [JsonPropertyName("subaccount")]
    public DydxSubaccount Subaccount { get; set; } = new();
}

public sealed class DydxSubaccount
{
    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    [JsonPropertyName("subaccountNumber")]
    public int SubaccountNumber { get; set; }

    [JsonPropertyName("equity")]
    [JsonConverter(typeof(StringDecimalConverter))]
    public decimal Equity { get; set; }

    [JsonPropertyName("freeCollateral")]
    [JsonConverter(typeof(StringDecimalConverter))]
    public decimal FreeCollateral { get; set; }
}

public sealed class DydxPositionsResponse
{
    [JsonPropertyName("positions")]
    public List<DydxPerpetualPosition> Positions { get; set; } = [];
}

public sealed class DydxPerpetualPosition
{
    [JsonPropertyName("market")]
    public string Market { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("side")]
    public string Side { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    [JsonConverter(typeof(StringDecimalConverter))]
    public decimal Size { get; set; }

    [JsonPropertyName("entryPrice")]
    [JsonConverter(typeof(StringDecimalConverter))]
    public decimal EntryPrice { get; set; }

    [JsonPropertyName("unrealizedPnl")]
    [JsonConverter(typeof(StringDecimalConverter))]
    public decimal UnrealizedPnl { get; set; }

    [JsonPropertyName("realizedPnl")]
    [JsonConverter(typeof(StringDecimalConverter))]
    public decimal RealizedPnl { get; set; }
}

// Validator response for account sequence
public sealed class DydxCosmosAccountResponse
{
    [JsonPropertyName("account")]
    public DydxCosmosAccountInfo Account { get; set; } = new();
}

public sealed class DydxCosmosAccountInfo
{
    [JsonPropertyName("account_number")]
    [JsonConverter(typeof(StringUInt64Converter))]
    public ulong AccountNumber { get; set; }

    [JsonPropertyName("sequence")]
    [JsonConverter(typeof(StringUInt64Converter))]
    public ulong Sequence { get; set; }
}

// Broadcast tx response
public sealed class DydxBroadcastResponse
{
    [JsonPropertyName("tx_response")]
    public DydxTxResponse TxResponse { get; set; } = new();
}

public sealed class DydxTxResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("txhash")]
    public string TxHash { get; set; } = string.Empty;

    [JsonPropertyName("raw_log")]
    public string RawLog { get; set; } = string.Empty;
}

// Block height response from Indexer
public sealed class DydxHeightResponse
{
    [JsonPropertyName("height")]
    [JsonConverter(typeof(StringUInt32Converter))]
    public uint Height { get; set; }

    [JsonPropertyName("time")]
    public string Time { get; set; } = string.Empty;
}

// ── JSON converters for string-encoded numeric fields ─────────────────────────

/// <summary>Converts JSON string values to decimal.</summary>
public sealed class StringDecimalConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            return decimal.TryParse(str, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : 0m;
        }
        return reader.GetDecimal();
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>Converts JSON string values to nullable decimal.</summary>
public sealed class StringNullableDecimalConverter : JsonConverter<decimal?>
{
    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;
        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (string.IsNullOrEmpty(str))
                return null;
            return decimal.TryParse(str, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : null;
        }
        return reader.GetDecimal();
    }

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}

/// <summary>Converts JSON string values to ulong.</summary>
public sealed class StringUInt64Converter : JsonConverter<ulong>
{
    public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            return ulong.TryParse(str, out var val) ? val : 0;
        }
        return reader.GetUInt64();
    }

    public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

/// <summary>Converts JSON string values to uint.</summary>
public sealed class StringUInt32Converter : JsonConverter<uint>
{
    public override uint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            return uint.TryParse(str, out var val) ? val : 0;
        }
        return reader.GetUInt32();
    }

    public override void Write(Utf8JsonWriter writer, uint value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

/// <summary>Converts JSON string values to int.</summary>
public sealed class StringInt32Converter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            return int.TryParse(str, out var val) ? val : 0;
        }
        return reader.GetInt32();
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
