using System.Diagnostics.CodeAnalysis;

namespace FundingRateArb.Domain.Enums;

[SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Long/Short are standard trading terms")]
public enum Side
{
    Long,
    Short
}
