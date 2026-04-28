using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Services;

/// <summary>
/// Generates deterministic <c>clientOrderId</c> values per (positionId, side, attemptN).
/// Format: <c>frb-{positionId:x}-{l|s}-{N}</c> — short prefix, lowercase hex positionId,
/// single-char side, integer attempt counter. Output is guaranteed ≤ 24 chars and uses
/// only [a-z0-9-] which fits every supported exchange's allowed character set.
///
/// Pure function: same (positionId, side, attemptN) → same string. ExecutionEngine
/// uses the same ID across retries so the exchange responds idempotently rather than
/// minting a new order.
///
/// Lighter is exempt (on-chain order entry; tx nonce is the idempotency primitive).
/// </summary>
public static class OrderIdGenerator
{
    /// <summary>The fixed 4-char prefix used by every bot-generated client order id (<c>frb-</c>).</summary>
    public const string BotPrefix = "frb-";

    /// <summary>Maximum length of the returned id; mirrors the smallest exchange cap with comfortable headroom.</summary>
    public const int MaxLength = 24;

    public static string For(int positionId, Side side, int attemptN)
    {
        if (positionId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(positionId), positionId, "positionId must be positive");
        }

        if (attemptN <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptN), attemptN, "attemptN must be positive");
        }

        var sideChar = side == Side.Long ? "l" : "s";
        var id = $"{BotPrefix}{positionId:x}-{sideChar}-{attemptN}";
        if (id.Length > MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(positionId),
                $"Generated client order id '{id}' exceeds {MaxLength}-char cap.");
        }

        return id;
    }

    /// <summary>True when the supplied exchange-reported client order id starts with <see cref="BotPrefix"/>.</summary>
    public static bool IsBotPrefixed(string? clientOrderId)
        => !string.IsNullOrEmpty(clientOrderId) && clientOrderId.StartsWith(BotPrefix, StringComparison.Ordinal);
}
