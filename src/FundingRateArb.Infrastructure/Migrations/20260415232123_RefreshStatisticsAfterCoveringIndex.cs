using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RefreshStatisticsAfterCoveringIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE STATISTICS dbo.FundingRateSnapshots;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op: statistics refresh is idempotent; no rollback needed.
        }
    }
}
