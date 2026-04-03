using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FundingRateArb.Web.Controllers;

[ApiController]
[Route("api/diagnostics")]
[EnableRateLimiting("auth")]
public class DiagnosticsController : ControllerBase
{
    private readonly IUnitOfWork _uow;
    private readonly IBotDiagnostics _diagnostics;
    private readonly IConfiguration _config;
    private readonly ILogger<DiagnosticsController> _logger;

    public DiagnosticsController(
        IUnitOfWork uow,
        IBotDiagnostics diagnostics,
        IConfiguration config,
        ILogger<DiagnosticsController> logger)
    {
        _uow = uow;
        _diagnostics = diagnostics;
        _config = config;
        _logger = logger;
    }

    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary(CancellationToken ct)
    {
        var apiKey = _config["Diagnostics:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            return StatusCode(503, new { error = "Diagnostics API key not configured" });
        }

        var providedKey = Request.Headers["X-Diagnostics-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(providedKey) || providedKey != apiKey)
        {
            return Unauthorized(new { error = "Invalid or missing X-Diagnostics-Key header" });
        }

        try
        {
            var summary = new DiagnosticsSummaryDto
            {
                GeneratedAt = DateTime.UtcNow
            };

            // Position status counts
            var statuses = Enum.GetValues<PositionStatus>();
            foreach (var status in statuses)
            {
                var count = await _uow.Positions.CountByStatusAsync(status);
                if (count > 0)
                {
                    summary.PositionStatusCounts[status.ToString()] = count;
                }
            }

            // Alert severity counts (last 24h)
            var severityCounts = await _uow.Alerts.GetSeverityCountsAsync(TimeSpan.FromHours(24));
            foreach (var (severity, count) in severityCounts)
            {
                summary.AlertSeverityCounts[severity.ToString()] = count;
            }

            // Circuit breaker states
            summary.CircuitBreakers = _diagnostics.GetCircuitBreakerStates().ToList();

            // Recent errors (last 24h, deduplicated)
            var recentAlerts = await _uow.Alerts.GetRecentUnreadAsync(TimeSpan.FromHours(24));
            var errorAlerts = recentAlerts
                .Where(a => a.Severity >= AlertSeverity.Warning)
                .GroupBy(a => a.Message)
                .Select(g => new RecentErrorDto
                {
                    Message = g.Key.Length > 200 ? g.Key[..200] + "..." : g.Key,
                    Severity = g.First().Severity.ToString(),
                    CreatedAt = g.Max(a => a.CreatedAt),
                    Count = g.Count()
                })
                .OrderByDescending(e => e.CreatedAt)
                .Take(20)
                .ToList();
            summary.RecentErrors = errorAlerts;

            // Recent closes with PnL (last 24h)
            var recentCloses = await _uow.Positions.GetClosedSinceAsync(DateTime.UtcNow.AddHours(-24));
            summary.RecentCloses = recentCloses
                .OrderByDescending(p => p.ClosedAt)
                .Take(20)
                .Select(p => new RecentCloseDto
                {
                    PositionId = p.Id,
                    Asset = p.Asset?.Symbol ?? "unknown",
                    CloseReason = p.CloseReason?.ToString() ?? "unknown",
                    PnlUsdc = p.RealizedPnl,
                    ClosedAt = p.ClosedAt ?? DateTime.MinValue
                })
                .ToList();

            // Funding rate freshness
            var latestRates = await _uow.FundingRates.GetLatestPerExchangePerAssetAsync();
            var now = DateTime.UtcNow;
            summary.FundingRateFreshness = latestRates
                .GroupBy(r => r.ExchangeId)
                .Select(g =>
                {
                    var latest = g.MaxBy(r => r.RecordedAt);
                    return new FundingRateFreshnessDto
                    {
                        ExchangeName = latest?.Exchange?.Name ?? $"Exchange-{g.Key}",
                        LastRecordedAt = latest?.RecordedAt ?? DateTime.MinValue,
                        StaleMinutes = latest is not null
                            ? (int)(now - latest.RecordedAt).TotalMinutes
                            : int.MaxValue
                    };
                })
                .ToList();

            return Ok(summary);
        }
        catch (Exception ex) when (ex.InnerException is System.Net.Sockets.SocketException
                                   || ex.Message.Contains("database", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(ex, "Database unreachable in diagnostics endpoint");
            return StatusCode(503, new { error = "Database unreachable" });
        }
    }
}
