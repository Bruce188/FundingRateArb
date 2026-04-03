using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Application.Interfaces;

public interface IBotDiagnostics
{
    IReadOnlyList<CircuitBreakerStatusDto> GetCircuitBreakerStates();
}
