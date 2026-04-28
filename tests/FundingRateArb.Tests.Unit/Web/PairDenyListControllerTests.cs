using System.Security.Claims;
using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Infrastructure.Repositories;
using FundingRateArb.Infrastructure.Services;
using FundingRateArb.Web.Areas.Admin.Controllers;
using FundingRateArb.Web.ViewModels.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Web;

public class PairDenyListControllerTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly IUnitOfWork _uow;
    private readonly PairDenyListProvider _provider;
    private readonly PairDenyListController _controller;
    private readonly string _dbName;

    private const string AdminUserId = "test-admin-user";

    public PairDenyListControllerTests()
    {
        _dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(_dbName)
            .Options;
        _context = new AppDbContext(options);
        var cache = new MemoryCache(new MemoryCacheOptions());
        _uow = new UnitOfWork(_context, cache);

        // Build minimal DI for PairDenyListProvider
        var services = new ServiceCollection();
        services.AddSingleton<IMemoryCache>(_ => cache);
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_dbName));
        services.AddScoped<IUnitOfWork>(sp =>
            new UnitOfWork(sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<IMemoryCache>()));
        var rootProvider = services.BuildServiceProvider();
        _provider = new PairDenyListProvider(rootProvider.GetRequiredService<IServiceScopeFactory>());

        // Seed a BotConfiguration so GetActiveAsync doesn't throw
        _context.BotConfigurations.Add(new BotConfiguration
        {
            IsEnabled = true,
            UpdatedByUserId = "admin",
        });
        _context.SaveChanges();

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, AdminUserId),
                new Claim(ClaimTypes.Role, "Admin"),
            }, "mock")),
        };

        _controller = new PairDenyListController(_uow, _provider, NullLogger<PairDenyListController>.Instance);
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        var tempDataProvider = Mock.Of<ITempDataProvider>();
        _controller.TempData = new TempDataDictionary(httpContext, tempDataProvider);
    }

    public void Dispose() => _context.Dispose();

    private static PairExecutionStats MakeRow(string longEx = "Hyperliquid", string shortEx = "Aster",
        bool isDenied = false)
        => new()
        {
            LongExchangeName = longEx,
            ShortExchangeName = shortEx,
            WindowStart = DateTime.UtcNow.AddDays(-14),
            WindowEnd = DateTime.UtcNow,
            CloseCount = 5,
            WinCount = 2,
            IsDenied = isDenied,
            DeniedReason = isDenied ? "auto: 0-win streak" : null,
            DeniedUntil = isDenied ? DateTime.UtcNow.AddDays(7) : null,
            LastUpdatedAt = DateTime.UtcNow,
        };

    [Fact]
    public async Task Index_ReturnsViewWithPopulatedModel()
    {
        _context.PairExecutionStats.AddRange(MakeRow("Hyperliquid", "Aster"), MakeRow("Aster", "Hyperliquid"));
        await _context.SaveChangesAsync();

        var result = await _controller.Index(CancellationToken.None);

        var view = result.Should().BeOfType<ViewResult>().Subject;
        var model = view.Model.Should().BeOfType<PairDenyListViewModel>().Subject;
        model.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Deny_PersistsRowAndRefreshesProvider()
    {
        var result = await _controller.Deny("Hyperliquid", "Aster", CancellationToken.None);

        // Redirect to Index
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("Index");

        // DB row persisted
        var row = await _context.PairExecutionStats.AsNoTracking()
            .FirstOrDefaultAsync(p => p.LongExchangeName == "Hyperliquid" && p.ShortExchangeName == "Aster");
        row.Should().NotBeNull();
        row!.IsDenied.Should().BeTrue();
        row.DeniedReason.Should().Be($"manual: {AdminUserId}");

        // Provider snapshot updated
        _provider.Current.IsDenied("Hyperliquid", "Aster").Should().BeTrue();
    }

    [Fact]
    public async Task UnDeny_ClearsRowAndRefreshesProvider()
    {
        // Pre-seed a denied row
        _context.PairExecutionStats.Add(MakeRow("Hyperliquid", "Aster", isDenied: true));
        await _context.SaveChangesAsync();
        await _provider.RefreshAsync(CancellationToken.None);
        _provider.Current.IsDenied("Hyperliquid", "Aster").Should().BeTrue();

        var result = await _controller.UnDeny("Hyperliquid", "Aster", CancellationToken.None);

        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("Index");

        var row = await _context.PairExecutionStats.AsNoTracking()
            .FirstOrDefaultAsync(p => p.LongExchangeName == "Hyperliquid" && p.ShortExchangeName == "Aster");
        row.Should().NotBeNull();
        row!.IsDenied.Should().BeFalse();
        row.DeniedUntil.Should().BeNull();
        row.DeniedReason.Should().BeNull();

        _provider.Current.IsDenied("Hyperliquid", "Aster").Should().BeFalse();
    }

    [Fact]
    public async Task Deny_WithBlankExchangeNames_ReturnsErrorTempData()
    {
        var result = await _controller.Deny("", "Aster", CancellationToken.None);

        result.Should().BeOfType<RedirectToActionResult>();
        _controller.TempData["Error"].Should().NotBeNull();

        var count = await _context.PairExecutionStats.CountAsync();
        count.Should().Be(0);
    }

    [Fact]
    public async Task UnDeny_NonExistentPair_ReturnsErrorTempData()
    {
        var result = await _controller.UnDeny("Nonexistent", "Exchange", CancellationToken.None);

        result.Should().BeOfType<RedirectToActionResult>();
        _controller.TempData["Error"].Should().NotBeNull();
    }
}
