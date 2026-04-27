using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FundingRateArb.Infrastructure.Repositories;

public class ReconciliationReportRepository : IReconciliationReportRepository
{
    private readonly AppDbContext _context;

    public ReconciliationReportRepository(AppDbContext context)
    {
        _context = context;
    }

    public Task<ReconciliationReport?> GetMostRecentAsync(CancellationToken ct = default)
        => _context.ReconciliationReports
            .OrderByDescending(r => r.RunAtUtc)
            .FirstOrDefaultAsync(ct);

    public void Add(ReconciliationReport report) =>
        _context.ReconciliationReports.Add(report);
}
