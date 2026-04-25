using System.Text.RegularExpressions;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Extensions;

public static class ArbitragePositionMappingExtensions
{
    private static readonly Regex PascalCaseRegex = new("(?<!^)([A-Z])", RegexOptions.Compiled);

    private static readonly Dictionary<CloseReason, string> CloseReasonDisplayNames =
        Enum.GetValues<CloseReason>()
            .ToDictionary(r => r, r => PascalCaseRegex.Replace(r.ToString(), " $1"));

    public static PositionSummaryDto ToSummaryDto(this ArbitragePosition pos)
    {
        return new PositionSummaryDto
        {
            Id = pos.Id,
            // Intentional: unified fallback format across all call sites (was string.Empty in Dashboard, "?" in BotOrchestrator)
            AssetSymbol = pos.Asset?.Symbol ?? $"Asset #{pos.AssetId}",
            LongExchangeName = pos.LongExchange?.Name ?? $"Exchange #{pos.LongExchangeId}",
            ShortExchangeName = pos.ShortExchange?.Name ?? $"Exchange #{pos.ShortExchangeId}",
            SizeUsdc = pos.SizeUsdc,
            MarginUsdc = pos.MarginUsdc,
            EntrySpreadPerHour = pos.EntrySpreadPerHour,
            CurrentSpreadPerHour = pos.CurrentSpreadPerHour,
            AccumulatedFunding = pos.AccumulatedFunding,
            // For open positions: PnL is computed live by the health monitor and overridden by the caller.
            // For closed positions: use RealizedPnl as the final settled value (no live mark price).
            UnrealizedPnl = 0m,
            ExchangePnl = IsClosedStatus(pos.Status) ? pos.RealizedPnl ?? 0m : 0m,
            UnifiedPnl = IsClosedStatus(pos.Status) ? pos.RealizedPnl ?? 0m : 0m,
            DivergencePct = pos.CurrentDivergencePct ?? 0m,
            PrevDivergencePct = pos.PrevDivergencePct,
            RealizedPnl = pos.RealizedPnl,
            CloseReason = pos.CloseReason,
            CloseReasonDisplayName = pos.CloseReason.HasValue ? ToDisplayName(pos.CloseReason.Value) : null,
            Status = pos.Status,
            OpenedAt = pos.OpenedAt,
            ConfirmedAtUtc = pos.ConfirmedAtUtc,
            ClosedAt = pos.ClosedAt,
            IsDryRun = pos.IsDryRun,
        };
    }

    public static PositionDetailsDto ToDetailsDto(this ArbitragePosition pos)
    {
        return new PositionDetailsDto
        {
            Id = pos.Id,
            AssetSymbol = pos.Asset?.Symbol ?? $"Asset #{pos.AssetId}",
            AssetId = pos.AssetId,
            LongExchangeName = pos.LongExchange?.Name ?? $"Exchange #{pos.LongExchangeId}",
            LongExchangeId = pos.LongExchangeId,
            ShortExchangeName = pos.ShortExchange?.Name ?? $"Exchange #{pos.ShortExchangeId}",
            ShortExchangeId = pos.ShortExchangeId,
            SizeUsdc = pos.SizeUsdc,
            MarginUsdc = pos.MarginUsdc,
            Leverage = pos.Leverage,
            LongEntryPrice = pos.LongEntryPrice,
            ShortEntryPrice = pos.ShortEntryPrice,
            EntrySpreadPerHour = pos.EntrySpreadPerHour,
            CurrentSpreadPerHour = pos.CurrentSpreadPerHour,
            AccumulatedFunding = pos.AccumulatedFunding,
            RealizedPnl = pos.RealizedPnl,
            RealizedDirectionalPnl = pos.RealizedDirectionalPnl,
            TotalFeesUsdc = pos.TotalFeesUsdc,
            EntryFeesUsdc = pos.EntryFeesUsdc,
            ExitFeesUsdc = pos.ExitFeesUsdc,
            CurrentDivergencePct = pos.CurrentDivergencePct,
            PrevDivergencePct = pos.PrevDivergencePct,
            Status = pos.Status,
            CloseReason = pos.CloseReason,
            OpenedAt = pos.OpenedAt,
            ConfirmedAtUtc = pos.ConfirmedAtUtc,
            ClosedAt = pos.ClosedAt,
            Notes = pos.Notes,
            IsDryRun = pos.IsDryRun,
        };
    }

    private static bool IsClosedStatus(PositionStatus status) =>
        status is PositionStatus.Closed or PositionStatus.EmergencyClosed or PositionStatus.Liquidated;

    /// <summary>
    /// Converts a PascalCase enum value to a space-separated display name.
    /// E.g. "DivergenceCritical" becomes "Divergence Critical".
    /// Uses pre-computed dictionary to avoid regex compilation per call.
    /// </summary>
    internal static string ToDisplayName(CloseReason reason)
    {
        return CloseReasonDisplayNames.TryGetValue(reason, out var name)
            ? name
            : PascalCaseRegex.Replace(reason.ToString(), " $1");
    }

    /// <summary>
    /// Returns the three-component PnL decomposition for a closed position per
    /// Analysis Section 4.7: (directional, funding, fees). Returns null while the position
    /// is still open. Strategy PnL should be reassembled from these components, never read
    /// from a single exchange's reported PnL field.
    /// </summary>
    public static PnlDecompositionDto? ToPnlDecomposition(this ArbitragePosition pos)
    {
        if (pos.RealizedDirectionalPnl is null)
        {
            return null;
        }

        return new PnlDecompositionDto(
            Directional: pos.RealizedDirectionalPnl.Value,
            Funding: pos.AccumulatedFunding,
            Fees: pos.TotalFeesUsdc);
    }
}
