namespace FundingRateArb.Domain.Enums;

public enum BotOperatingState
{
    Stopped = 0,    // Monitoring only. Zero order placement.
    Armed = 1,      // Scanning. Orders allowed when conditions met.
    Trading = 2,    // Actively managing open positions.
    Paused = 3      // Emergency pause. No new positions, existing monitored.
}
