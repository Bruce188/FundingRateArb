using System.Reflection;
using FluentAssertions;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Infrastructure.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace FundingRateArb.Tests.Unit.Repositories;

public class FundingRateRepositoryIndexingTests
{
    private const string IndexName = "IX_FundingRateSnapshots_Exchange_Asset_Recorded";

    // Build an AppDbContext using UseSqlServer (clearly-fake connection string — model-only, never opened)
    // so that SQL Server-specific annotations (SqlServer:Include, IsDescending) are present.
    // Use IDesignTimeModel to access the full design-time metadata (not the read-optimized runtime model).
    // The model is immutable; build it once and reuse across all tests in this class.
    private static readonly Lazy<IModel> _model = new(() =>
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(none);Database=ModelOnly;")
            .Options;
        using var context = new AppDbContext(options);
        return context.GetService<IDesignTimeModel>().Model;
    });

    [Fact]
    public void CoveringIndex_HasCorrectKeyColumns()
    {
        var entityType = _model.Value.FindEntityType(typeof(FundingRateSnapshot))!;
        var index = entityType.GetIndexes().FirstOrDefault(i => i.GetDatabaseName() == IndexName);
        index.Should().NotBeNull($"expected index '{IndexName}' to exist on FundingRateSnapshot");

        var columnNames = index!.Properties.Select(p => p.Name).ToList();
        columnNames.Should().Equal("ExchangeId", "AssetId", "RecordedAt");
    }

    [Fact]
    public void CoveringIndex_HasDescendingRecordedAt()
    {
        var entityType = _model.Value.FindEntityType(typeof(FundingRateSnapshot))!;
        var index = entityType.GetIndexes().FirstOrDefault(i => i.GetDatabaseName() == IndexName);
        index.Should().NotBeNull($"expected index '{IndexName}' to exist on FundingRateSnapshot");

        index!.IsDescending.Should().Equal(false, false, true);
    }

    [Fact]
    public void CoveringIndex_IncludesMarkPriceAndRatePerHour()
    {
        var entityType = _model.Value.FindEntityType(typeof(FundingRateSnapshot))!;
        var index = entityType.GetIndexes().FirstOrDefault(i => i.GetDatabaseName() == IndexName);
        index.Should().NotBeNull($"expected index '{IndexName}' to exist on FundingRateSnapshot");

        var includeColumns = (string[]?)index!.FindAnnotation("SqlServer:Include")?.Value;
        includeColumns.Should().NotBeNull();
        includeColumns.Should().BeEquivalentTo(new[] { "MarkPrice", "RatePerHour" });
    }

    [Fact]
    public void Up_ExecutesUpdateStatisticsOnFundingRateSnapshots()
    {
        var migration = new RefreshStatisticsAfterCoveringIndex();
        var builder = new MigrationBuilder("SqlServer");
        var upMethod = typeof(RefreshStatisticsAfterCoveringIndex)
            .GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic);
        upMethod.Should().NotBeNull("migration should have a protected Up method");
        upMethod!.Invoke(migration, [builder]);

        builder.Operations.Should().ContainSingle()
            .Which.Should().BeOfType<SqlOperation>()
            .Which.Sql.Should().Be("UPDATE STATISTICS dbo.FundingRateSnapshots;");
    }

    [Fact]
    public void Down_ProducesNoOperations()
    {
        var migration = new RefreshStatisticsAfterCoveringIndex();
        var builder = new MigrationBuilder("SqlServer");
        var downMethod = typeof(RefreshStatisticsAfterCoveringIndex)
            .GetMethod("Down", BindingFlags.Instance | BindingFlags.NonPublic);
        downMethod.Should().NotBeNull("migration should have a protected Down method");
        downMethod!.Invoke(migration, [builder]);

        builder.Operations.Should().BeEmpty();
    }
}
