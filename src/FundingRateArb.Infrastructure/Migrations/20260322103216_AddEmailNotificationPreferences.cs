using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundingRateArb.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddEmailNotificationPreferences : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "EmailCriticalAlerts",
            table: "UserConfigurations",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "EmailDailySummary",
            table: "UserConfigurations",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "EmailNotificationsEnabled",
            table: "UserConfigurations",
            type: "bit",
            nullable: false,
            defaultValue: false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "EmailCriticalAlerts",
            table: "UserConfigurations");

        migrationBuilder.DropColumn(
            name: "EmailDailySummary",
            table: "UserConfigurations");

        migrationBuilder.DropColumn(
            name: "EmailNotificationsEnabled",
            table: "UserConfigurations");
    }
}
