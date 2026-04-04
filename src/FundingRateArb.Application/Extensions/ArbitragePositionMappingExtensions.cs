using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Extensions;

public static class ArbitragePositionMappingExtensions
{
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
            // PnL values are computed live in the health monitor loop and set by the caller
            UnrealizedPnl = 0m,
            ExchangePnl = 0m,
            UnifiedPnl = 0m,
            DivergencePct = pos.CurrentDivergencePct ?? 0m,
            RealizedPnl = pos.RealizedPnl,
            Status = pos.Status,
            OpenedAt = pos.OpenedAt,
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
            Status = pos.Status,
            CloseReason = pos.CloseReason,
            OpenedAt = pos.OpenedAt,
            ClosedAt = pos.ClosedAt,
            Notes = pos.Notes,
            IsDryRun = pos.IsDryRun,
        };
    }
}
