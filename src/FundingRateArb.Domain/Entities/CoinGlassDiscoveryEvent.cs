using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Domain.Entities;

public class CoinGlassDiscoveryEvent
{
    public int Id { get; set; }
    public DiscoveryEventType EventType { get; set; }
    public string ExchangeName { get; set; } = null!;
    public string? Symbol { get; set; }
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;
}
