namespace FundingRateArb.Application.DTOs;

public record ExchangeOverviewDto
{
    public string ExchangeName { get; init; } = null!;
    public int CoinCount { get; init; }
    public bool HasDirectConnector { get; init; }
    public bool IsPlanned { get; init; }
    public string StatusBadge { get; init; } = null!;
    public DateTime LastSeen { get; init; }
}

public record SpreadOpportunityDto
{
    public string Symbol { get; init; } = null!;
    public string LongExchange { get; init; } = null!;
    public string ShortExchange { get; init; } = null!;
    public decimal SpreadPerHour { get; init; }
    public decimal EstFeesPerHour { get; init; }
    public decimal NetYieldPerHour { get; init; }
    public decimal Apr { get; init; }
    public bool BothHaveConnectors { get; init; }
    public bool OneHasConnector { get; init; }
    public string ConnectorStatus { get; init; } = null!;
}

public record RateComparisonDto
{
    public string Symbol { get; init; } = null!;
    public string ExchangeName { get; init; } = null!;
    public decimal DirectRate { get; init; }
    public decimal CoinGlassRate { get; init; }
    public decimal DivergencePercent { get; init; }
    public bool IsWarning { get; init; }
}

public record DiscoveryEventDto
{
    public string EventType { get; init; } = null!;
    public string ExchangeName { get; init; } = null!;
    public string? Symbol { get; init; }
    public DateTime DiscoveredAt { get; init; }
}
