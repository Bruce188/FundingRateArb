using FundingRateArb.Application.Common.Services;
using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.Services;

public class DatabaseSpaceHealthProbe : IDatabaseSpaceHealthProbe
{
    private readonly AppDbContext _db;
    private readonly ILogger<DatabaseSpaceHealthProbe> _logger;

    public DatabaseSpaceHealthProbe(AppDbContext db, ILogger<DatabaseSpaceHealthProbe> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<double> GetUsedSpaceRatioAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Query sys.database_files to compute used/max_size ratio for the primary data file.
            // Returns 0.0 if max_size is unbounded (-1) or result is null.
            const string sql = """
                SELECT TOP 1
                  CASE
                    WHEN size = 0 OR max_size = -1 THEN CAST(0.0 AS float)
                    ELSE CAST(FILEPROPERTY(name, 'SpaceUsed') AS float) * 8192.0
                         / NULLIF(CAST(size AS float) * 8192.0, 0)
                  END
                FROM sys.database_files
                WHERE type_desc = 'ROWS'
                ORDER BY file_id
                """;

            var results = await _db.Database
                .SqlQueryRaw<double>(sql)
                .ToListAsync(cancellationToken);

            return results.Count > 0 ? results[0] : 0.0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DatabaseSpaceHealthProbe failed to query sys.database_files; returning 0.0");
            return 0.0;
        }
    }
}
