namespace FundingRateArb.Application.Interfaces;

public interface IBotControl
{
    void ClearCooldowns();
    void TriggerImmediateCycle();
}
