using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Application.Interfaces;

public interface IEmailService
{
    Task SendAlertEmailAsync(string recipientEmail, string subject, string body, CancellationToken ct = default);
    Task SendDailySummaryAsync(string recipientEmail, DailySummaryDto summary, CancellationToken ct = default);
}
