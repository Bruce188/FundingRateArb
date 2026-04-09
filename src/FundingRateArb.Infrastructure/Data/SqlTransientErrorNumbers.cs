namespace FundingRateArb.Infrastructure.Data;

/// <summary>
/// Centralized list of SQL Server error numbers treated as transient login-phase
/// or connectivity failures in Azure. Used by DbContext retry configuration, the
/// database health check, and the repository catch-to-degrade path so they all
/// share the same allowlist.
///
/// Covers: -2 (timeout), 35/64/233 (transport close), 10053/10054/10060
/// (socket reset / peer closed / connect timeout), 10928/10929 (resource limit),
/// 40197/40501/40613 (Azure SQL service busy / not available).
/// </summary>
public static class SqlTransientErrorNumbers
{
    public static readonly int[] All =
    [
        -2, 35, 64, 233, 10053, 10054, 10060, 10928, 10929, 40197, 40501, 40613,
    ];

    private static readonly HashSet<int> Lookup = [.. All];

    public static bool Contains(int errorNumber) => Lookup.Contains(errorNumber);
}
