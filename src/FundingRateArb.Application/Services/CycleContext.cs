using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Services;

/// <summary>
/// Bundles per-cycle scoped dependencies resolved in RunCycleAsync.
/// Passed to ExecuteUserCycleAsync to reduce parameter count.
/// </summary>
public record CycleContext(
    IUnitOfWork Uow,
    BotConfiguration GlobalConfig,
    IPositionHealthMonitor HealthMonitor,
    ISignalEngine SignalEngine,
    IExecutionEngine ExecutionEngine,
    IUserSettingsService UserSettings);
