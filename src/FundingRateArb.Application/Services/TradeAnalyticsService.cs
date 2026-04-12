using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Services;

public class TradeAnalyticsService : ITradeAnalyticsService
{
    private readonly IUnitOfWork _uow;

    public TradeAnalyticsService(IUnitOfWork uow) => _uow = uow;

    public async Task<PositionAnalyticsDto?> GetPositionAnalyticsAsync(int positionId, string? userId = null, CancellationToken ct = default)
    {
        var position = await _uow.Positions.GetByIdAsync(positionId);
        if (position is null)
        {
            return null;
        }

        if (userId is not null && position.UserId != userId)
        {
            return null;
        }

        var hoursHeld = ComputeHoursHeld(position);
        var actualPnl = ComputeActualPnl(position);
        var projectedPnl = ComputeProjectedPnl(position, hoursHeld);

        // Fetch spread history from hourly aggregates
        var spreadHistory = await GetSpreadHistoryAsync(position, ct);

        var isClosed = position.Status is PositionStatus.Closed or PositionStatus.EmergencyClosed or PositionStatus.Liquidated;
        var counterfactuals = isClosed && position.ClosedAt.HasValue
            ? await ComputeCounterfactualPnlAsync(position, actualPnl, ct)
            : [];

        return new PositionAnalyticsDto
        {
            PositionId = position.Id,
            AssetSymbol = position.Asset?.Symbol ?? $"#{position.AssetId}",
            LongExchangeName = position.LongExchange?.Name ?? $"#{position.LongExchangeId}",
            ShortExchangeName = position.ShortExchange?.Name ?? $"#{position.ShortExchangeId}",
            ActualPnl = actualPnl,
            ProjectedPnl = projectedPnl,
            PnlDifference = actualPnl - projectedPnl,
            HoursHeld = hoursHeld,
            EntrySpreadPerHour = position.EntrySpreadPerHour,
            SizeUsdc = position.SizeUsdc,
            IsClosed = isClosed,
            OpenedAt = position.OpenedAt,
            ConfirmedAtUtc = position.ConfirmedAtUtc,
            ClosedAt = position.ClosedAt,
            SpreadHistory = spreadHistory,
            Counterfactuals = counterfactuals,
        };
    }

    public async Task<List<PositionAnalyticsSummaryDto>> GetAllPositionAnalyticsAsync(
        string? userId, int skip, int take, CancellationToken ct = default)
    {
        var positions = userId is null
            ? await _uow.Positions.GetAllAsync(skip, take)
            : await _uow.Positions.GetByUserAsync(userId, skip, take);

        return positions.Select(pos =>
        {
            var hoursHeld = ComputeHoursHeld(pos);
            var actualPnl = ComputeActualPnl(pos);
            var projectedPnl = ComputeProjectedPnl(pos, hoursHeld);
            var difference = actualPnl - projectedPnl;

            return new PositionAnalyticsSummaryDto
            {
                PositionId = pos.Id,
                AssetSymbol = pos.Asset?.Symbol ?? $"#{pos.AssetId}",
                LongExchangeName = pos.LongExchange?.Name ?? $"#{pos.LongExchangeId}",
                ShortExchangeName = pos.ShortExchange?.Name ?? $"#{pos.ShortExchangeId}",
                ActualPnl = actualPnl,
                ProjectedPnl = projectedPnl,
                PnlDifference = difference,
                HoursHeld = hoursHeld,
                IsClosed = pos.Status is PositionStatus.Closed or PositionStatus.EmergencyClosed or PositionStatus.Liquidated,
                StatusLabel = pos.Status.ToString(),
                OpenedAt = pos.OpenedAt,
                AccuracyPct = projectedPnl != 0 ? (actualPnl / projectedPnl) * 100m : null,
            };
        }).ToList();
    }

    public static decimal ComputeHoursHeld(ArbitragePosition pos)
    {
        var end = pos.ClosedAt ?? DateTime.UtcNow;
        return (decimal)(end - pos.OpenedAt).TotalHours;
    }

    public static decimal ComputeActualPnl(ArbitragePosition pos) =>
        pos.RealizedPnl ?? pos.AccumulatedFunding;

    public static decimal ComputeProjectedPnl(ArbitragePosition pos, decimal hoursHeld) =>
        pos.SizeUsdc * pos.EntrySpreadPerHour * hoursHeld;

    /// <summary>
    /// Computes hypothetical PnL at +1h, +4h, +24h, +48h past close using post-close spread data.
    /// Bounded by close time + 48h max to prevent unbounded history scans.
    /// </summary>
    internal async Task<List<CounterfactualPoint>> ComputeCounterfactualPnlAsync(
        ArbitragePosition position, decimal actualPnl, CancellationToken ct)
    {
        if (!position.ClosedAt.HasValue)
        {
            return [];
        }

        var closeTime = position.ClosedAt.Value;
        var maxWindow = TimeSpan.FromHours(48);
        var horizons = new (string Label, decimal Hours)[]
        {
            ("+1h", 1m), ("+4h", 4m), ("+24h", 24m), ("+48h", 48m),
        };

        // Fetch post-close spread data (bounded to 48h)
        var longAggs = await _uow.FundingRates.GetHourlyAggregatesAsync(
            position.AssetId, position.LongExchangeId, closeTime, closeTime + maxWindow, ct);
        var shortAggs = await _uow.FundingRates.GetHourlyAggregatesAsync(
            position.AssetId, position.ShortExchangeId, closeTime, closeTime + maxWindow, ct);

        var shortByHour = shortAggs.ToDictionary(a => a.HourUtc, a => a.AvgRatePerHour);

        // Compute cumulative spread per hour for each time horizon
        var spreadPoints = longAggs
            .Where(a => shortByHour.ContainsKey(a.HourUtc))
            .OrderBy(a => a.HourUtc)
            .Select(a => new { a.HourUtc, Spread = shortByHour[a.HourUtc] - a.AvgRatePerHour })
            .ToList();

        var results = new List<CounterfactualPoint>();
        foreach (var (label, hours) in horizons)
        {
            var cutoff = closeTime.AddHours((double)hours);
            var cumulativeSpread = spreadPoints
                .Where(p => p.HourUtc < cutoff)
                .Sum(p => p.Spread);

            // Hypothetical PnL = actual PnL + additional spread earnings if held longer
            var hypotheticalPnl = actualPnl + (cumulativeSpread * position.SizeUsdc);
            results.Add(new CounterfactualPoint
            {
                Label = label,
                HoursAfterClose = hours,
                HypotheticalPnl = hypotheticalPnl,
            });
        }

        return results;
    }

    private async Task<List<HourlySpreadPoint>> GetSpreadHistoryAsync(
        ArbitragePosition pos, CancellationToken ct)
    {
        var from = pos.OpenedAt;
        var to = pos.ClosedAt ?? DateTime.UtcNow;

        // Fetch hourly aggregates for both legs
        var longAggs = await _uow.FundingRates.GetHourlyAggregatesAsync(
            pos.AssetId, pos.LongExchangeId, from, to, ct);
        var shortAggs = await _uow.FundingRates.GetHourlyAggregatesAsync(
            pos.AssetId, pos.ShortExchangeId, from, to, ct);

        // Build a lookup for short exchange rates by hour
        var shortByHour = shortAggs.ToDictionary(a => a.HourUtc, a => a.AvgRatePerHour);

        // Compute spread per hour for each hour we have long-exchange data
        return longAggs
            .Where(a => shortByHour.ContainsKey(a.HourUtc))
            .Select(a => new HourlySpreadPoint
            {
                HourUtc = a.HourUtc,
                // Spread = short rate - long rate (short earns funding, long pays)
                SpreadPerHour = shortByHour[a.HourUtc] - a.AvgRatePerHour,
            })
            .OrderBy(p => p.HourUtc)
            .ToList();
    }
}
