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
            AssetSymbol = pos.Asset?.Symbol ?? $"Asset #{pos.AssetId}",
            LongExchangeName = pos.LongExchange?.Name ?? $"Exchange #{pos.LongExchangeId}",
            ShortExchangeName = pos.ShortExchange?.Name ?? $"Exchange #{pos.ShortExchangeId}",
            SizeUsdc = pos.SizeUsdc,
            MarginUsdc = pos.MarginUsdc,
            EntrySpreadPerHour = pos.EntrySpreadPerHour,
            CurrentSpreadPerHour = pos.CurrentSpreadPerHour,
            AccumulatedFunding = pos.AccumulatedFunding,
            UnrealizedPnl = pos.AccumulatedFunding, // best estimate until live mark-to-market
            RealizedPnl = pos.RealizedPnl,
            Status = pos.Status,
            OpenedAt = pos.OpenedAt,
            ClosedAt = pos.ClosedAt,
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
        };
    }
}
