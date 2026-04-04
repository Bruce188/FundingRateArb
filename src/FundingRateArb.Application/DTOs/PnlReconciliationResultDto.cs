namespace FundingRateArb.Application.DTOs;

public record PnlReconciliationResultDto
{
    public decimal? ExchangeReportedPnl { get; init; }
    public decimal? PnlDivergencePercent { get; init; }
    public decimal? ExchangeReportedFees { get; init; }
    public decimal? ExchangeReportedFunding { get; init; }
    public bool IsReconciled { get; init; }
}
