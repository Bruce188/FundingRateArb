using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Services;

public class ExecutionEngine : IExecutionEngine
{
    private readonly IUnitOfWork _uow;
    private readonly IExchangeConnectorFactory _connectorFactory;

    public ExecutionEngine(IUnitOfWork uow, IExchangeConnectorFactory connectorFactory)
    {
        _uow = uow;
        _connectorFactory = connectorFactory;
    }

    public Task<(bool Success, string? Error)> OpenPositionAsync(ArbitrageOpportunityDto opp, decimal sizeUsdc)
        => throw new NotImplementedException();

    public Task ClosePositionAsync(ArbitragePosition position, CloseReason reason)
        => throw new NotImplementedException();
}
