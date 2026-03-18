namespace FundingRateArb.Application.Services;

public interface IPositionHealthMonitor
{
    Task CheckAndActAsync();
}
