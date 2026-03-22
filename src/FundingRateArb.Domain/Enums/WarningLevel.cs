namespace FundingRateArb.Domain.Enums;

/// <summary>
/// Urgency level for position warnings. Maps to Bootstrap contextual row classes:
/// None = default, Info = table-info, Warning = table-warning, Critical = table-danger.
/// </summary>
public enum WarningLevel
{
    None = 0,
    Info = 1,
    Warning = 2,
    Critical = 3
}
