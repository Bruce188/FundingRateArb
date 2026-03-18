using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FundingRateArb.Infrastructure.Repositories;

public class ExchangeRepository : IExchangeRepository
{
    private readonly AppDbContext _context;

    public ExchangeRepository(AppDbContext context) => _context = context;

    public Task<List<Exchange>> GetAllAsync() =>
        _context.Exchanges.ToListAsync();

    public Task<List<Exchange>> GetActiveAsync() =>
        _context.Exchanges.Where(e => e.IsActive).ToListAsync();

    public Task<Exchange?> GetByIdAsync(int id) =>
        _context.Exchanges.FirstOrDefaultAsync(e => e.Id == id);

    public Task<Exchange?> GetByNameAsync(string name) =>
        _context.Exchanges.FirstOrDefaultAsync(e => e.Name == name);

    public void Add(Exchange exchange) => _context.Exchanges.Add(exchange);

    public void Update(Exchange exchange) => _context.Exchanges.Update(exchange);

    public void Remove(Exchange exchange) => _context.Exchanges.Remove(exchange);
}
