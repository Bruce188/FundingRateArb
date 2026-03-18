using FundingRateArb.Application.Common.Repositories;

namespace FundingRateArb.Application.Services;

public class PositionHealthMonitor : IPositionHealthMonitor
{
    private readonly IUnitOfWork _uow;
    private readonly IExecutionEngine _executionEngine;

    public PositionHealthMonitor(IUnitOfWork uow, IExecutionEngine executionEngine)
    {
        _uow = uow;
        _executionEngine = executionEngine;
    }

    public Task CheckAndActAsync()
        => throw new NotImplementedException();
}
