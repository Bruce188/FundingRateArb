namespace FundingRateArb.Application.Services;

public interface IPositionHealthMonitor
{
    Task CheckAndActAsync(CancellationToken ct = default);
}
