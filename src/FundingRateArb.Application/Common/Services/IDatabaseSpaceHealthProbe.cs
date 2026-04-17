namespace FundingRateArb.Application.Common.Services;

public interface IDatabaseSpaceHealthProbe
{
    /// <summary>
    /// Returns used-space ratio of the primary data file in [0.0, 1.0].
    /// Returns 0.0 if max_size is unbounded or the probe cannot be computed.
    /// </summary>
    Task<double> GetUsedSpaceRatioAsync(CancellationToken cancellationToken);
}
