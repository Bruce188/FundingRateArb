using System.Collections.Concurrent;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace FundingRateArb.Infrastructure.Services;

public class EmailService : IEmailService
{
    private readonly ILogger<EmailService> _logger;
    private readonly string? _apiKey;
    private readonly string _fromEmail;
    private readonly string _fromName;

    /// <summary>Rate limiter: tracks last email sent per (userId, subject-prefix) to enforce 15-minute gaps.</summary>
    private readonly ConcurrentDictionary<string, DateTime> _lastSentAt = new();
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(15);

    public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
    {
        _logger = logger;
        _apiKey = configuration["SendGrid:ApiKey"];
        _fromEmail = configuration["SendGrid:FromEmail"] ?? "noreply@fundingratebot.com";
        _fromName = configuration["SendGrid:FromName"] ?? "FundingRateArb";
    }

    public async Task SendAlertEmailAsync(string recipientEmail, string subject, string body, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            _logger.LogDebug("SendGrid API key not configured — skipping alert email for {Email}", recipientEmail);
            return;
        }

        // Rate limit: max 1 alert email per 15 minutes per (email, subject)
        var rateLimitKey = $"{recipientEmail}:alert";
        if (_lastSentAt.TryGetValue(rateLimitKey, out var lastSent) && DateTime.UtcNow - lastSent < RateLimitWindow)
        {
            _logger.LogDebug(
                "Rate limiting alert email for {Email} — last sent {Ago:F0}s ago",
                recipientEmail, (DateTime.UtcNow - lastSent).TotalSeconds);
            return;
        }

        try
        {
            var client = new SendGridClient(_apiKey);
            var msg = new SendGridMessage
            {
                From = new EmailAddress(_fromEmail, _fromName),
                Subject = subject,
                HtmlContent = WrapInTemplate(subject, body),
            };
            msg.AddTo(new EmailAddress(recipientEmail));

            var response = await client.SendEmailAsync(msg, ct);

            if (response.IsSuccessStatusCode)
            {
                _lastSentAt[rateLimitKey] = DateTime.UtcNow;
                _logger.LogInformation("Alert email sent to {Email}: {Subject}", recipientEmail, subject);
            }
            else
            {
                var responseBody = await response.Body.ReadAsStringAsync(ct);
                _logger.LogWarning("SendGrid returned {StatusCode} for {Email}: {Body}",
                    response.StatusCode, recipientEmail, responseBody);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send alert email to {Email}", recipientEmail);
        }
    }

    public async Task SendDailySummaryAsync(string recipientEmail, DailySummaryDto summary, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            _logger.LogDebug("SendGrid API key not configured — skipping daily summary for {Email}", recipientEmail);
            return;
        }

        var subject = $"FundingRateArb Daily Summary — {DateTime.UtcNow:yyyy-MM-dd}";
        var body = $@"
            <h3>Daily Summary</h3>
            <table style='border-collapse: collapse; width: 100%; max-width: 500px;'>
                <tr><td style='padding: 4px 8px;'>Open Positions</td><td style='padding: 4px 8px; text-align: right;'><strong>{summary.OpenPositionCount}</strong></td></tr>
                <tr><td style='padding: 4px 8px;'>Total PnL</td><td style='padding: 4px 8px; text-align: right;'><strong>{summary.TotalPnl:F2} USDC</strong></td></tr>
                <tr><td style='padding: 4px 8px;'>Closed Today</td><td style='padding: 4px 8px; text-align: right;'>{summary.ClosedTodayCount}</td></tr>
                <tr><td style='padding: 4px 8px;'>Realized PnL Today</td><td style='padding: 4px 8px; text-align: right;'>{summary.RealizedPnlToday:F2} USDC</td></tr>
                <tr><td style='padding: 4px 8px;'>Alerts</td><td style='padding: 4px 8px; text-align: right;'>{summary.AlertsCount}</td></tr>
                <tr><td style='padding: 4px 8px;'>Best Spread</td><td style='padding: 4px 8px; text-align: right;'>{summary.BestAvailableSpread * 100:F4}%/hr{(summary.BestOpportunityAsset is not null ? $" ({summary.BestOpportunityAsset})" : "")}</td></tr>
            </table>";

        try
        {
            var client = new SendGridClient(_apiKey);
            var msg = new SendGridMessage
            {
                From = new EmailAddress(_fromEmail, _fromName),
                Subject = subject,
                HtmlContent = WrapInTemplate(subject, body),
            };
            msg.AddTo(new EmailAddress(recipientEmail));

            var response = await client.SendEmailAsync(msg, ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Daily summary email sent to {Email}", recipientEmail);
            }
            else
            {
                _logger.LogWarning("SendGrid returned {StatusCode} for daily summary to {Email}",
                    response.StatusCode, recipientEmail);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send daily summary email to {Email}", recipientEmail);
        }
    }

    private static string WrapInTemplate(string title, string body)
    {
        return $@"
            <div style='font-family: -apple-system, BlinkMacSystemFont, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px;'>
                <div style='background: #1a1a2e; color: #ffc107; padding: 16px; border-radius: 8px 8px 0 0;'>
                    <h2 style='margin: 0;'>FundingRateArb</h2>
                </div>
                <div style='background: #f8f9fa; padding: 20px; border: 1px solid #dee2e6; border-top: none; border-radius: 0 0 8px 8px;'>
                    {body}
                </div>
                <p style='color: #6c757d; font-size: 12px; margin-top: 16px; text-align: center;'>
                    You are receiving this because you enabled email notifications in your FundingRateArb settings.
                </p>
            </div>";
    }
}
