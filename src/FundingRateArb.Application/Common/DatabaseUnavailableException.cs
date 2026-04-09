namespace FundingRateArb.Application.Common;

/// <summary>
/// Raised by the data-access layer when a transient database outage prevents
/// a read from completing. Callers in the Application layer catch this to
/// surface a degraded result instead of propagating a provider-specific
/// exception (e.g. Microsoft.Data.SqlClient.SqlException) across layer
/// boundaries.
/// </summary>
public sealed class DatabaseUnavailableException : Exception
{
    public DatabaseUnavailableException(string message)
        : base(message)
    {
    }

    public DatabaseUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
