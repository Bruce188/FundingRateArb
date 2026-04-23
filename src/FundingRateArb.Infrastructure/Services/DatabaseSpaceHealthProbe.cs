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
                    WHEN max_size = -1 OR max_size = 0 THEN CAST(0.0 AS float)
                    ELSE CAST(FILEPROPERTY(name, 'SpaceUsed') AS float) * 8192.0
                         / NULLIF(CAST(max_size AS float) * 8192.0, 0)
                  END
                FROM sys.database_files
                WHERE type_desc = 'ROWS'
                ORDER BY file_id
                """;

            return await _db.Database
                .SqlQueryRaw<double>(sql)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database space probe failed: {Message}", ex.Message);
            return 0.0;
        }
    }
}
