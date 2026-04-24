using FluentAssertions;

namespace FundingRateArb.Tests.Integration;

/// <summary>
/// Tests for the flag-gated Production seed branch in Program.cs.
///
/// Strategy: source-code inspection tests.
/// Program.cs contains inline startup code (seed/migration block) that is tightly coupled
/// to the ASP.NET Core host startup lifecycle. The Production Serilog sink (MSSqlServer with
/// AutoCreateSqlTable=true) establishes a live SQL Server connection during Build(), making
/// full WebApplicationFactory-based integration tests for the non-Development paths impractical
/// without a real SQL Server. Source-code inspection is the canonical testing pattern for this
/// category of startup code. These tests assert the STRUCTURAL requirements from the spec:
///
///   1. The existing IsDevelopment block is preserved exactly.
///   2. A new else-if branch exists that fires when BOTH flags are set.
///   3. The else-if condition tests Seed:ForceAdminPasswordReset=true AND
///      !string.IsNullOrWhiteSpace(Seed:AdminPassword).
///   4. Inside the else-if: DbSeeder.SeedAsync is called; db.Database.Migrate/MigrateAsync is NOT.
///   5. The log message "ForceAdminPasswordReset executed" is emitted.
///   6. The original Skipping else branch is preserved.
///   7. The value of Seed:AdminPassword is never logged.
/// </summary>
public class ProductionSeedBranchTests
{
    private static readonly string ProgramCsPath = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "FundingRateArb.Web", "Program.cs"));

    private static string ProgramContent => File.ReadAllText(ProgramCsPath);

    // -------------------------------------------------------------------------
    // 1. Presence of the else-if block
    // -------------------------------------------------------------------------

    [Fact]
    public void ProgramCs_ContainsElseIfBranchForProductionSeed()
    {
        // The implementation must have an `else if` after the IsDevelopment block.
        ProgramContent.Should().Contain(
            "else if",
            "Program.cs must contain an else-if branch for the Production seed path");
    }

    // -------------------------------------------------------------------------
    // 2. Condition: Seed:ForceAdminPasswordReset
    // -------------------------------------------------------------------------

    [Fact]
    public void ProgramCs_ElseIfCondition_ChecksForceAdminPasswordResetFlag()
    {
        // The else-if must gate on GetValue<bool>("Seed:ForceAdminPasswordReset")
        ProgramContent.Should().Contain(
            "Seed:ForceAdminPasswordReset",
            "the else-if condition must read Seed:ForceAdminPasswordReset from configuration");
    }

    [Fact]
    public void ProgramCs_ElseIfCondition_UsesGetValueBool()
    {
        // The flag must be read as a bool, not a string comparison
        ProgramContent.Should().MatchRegex(
            @"GetValue\s*<\s*bool\s*>\s*\(\s*""Seed:ForceAdminPasswordReset""",
            "the ForceAdminPasswordReset flag must be read via GetValue<bool>()");
    }

    // -------------------------------------------------------------------------
    // 3. Condition: Seed:AdminPassword must not be null/whitespace
    // -------------------------------------------------------------------------

    [Fact]
    public void ProgramCs_ElseIfCondition_ChecksAdminPasswordNotNullOrWhiteSpace()
    {
        // IsNullOrWhiteSpace is already used elsewhere in Program.cs (e.g. for the AI
        // connection string). This test specifically requires it to be paired with the
        // Seed:AdminPassword config key — asserting that the new else-if guard uses it.
        ProgramContent.Should().MatchRegex(
            @"IsNullOrWhiteSpace[^)]*Seed:AdminPassword|Seed:AdminPassword[^;]*IsNullOrWhiteSpace",
            "the else-if condition must call IsNullOrWhiteSpace on the Seed:AdminPassword config value");
    }

    [Fact]
    public void ProgramCs_ElseIfCondition_ChecksAdminPasswordConfigKey()
    {
        // The AdminPassword key is referenced in the guard
        ProgramContent.Should().MatchRegex(
            @"IsNullOrWhiteSpace.*Seed:AdminPassword|Seed:AdminPassword.*IsNullOrWhiteSpace",
            "the IsNullOrWhiteSpace check must reference the Seed:AdminPassword config key");
    }

    // -------------------------------------------------------------------------
    // 4a. Inside the else-if: DbSeeder.SeedAsync is called
    // -------------------------------------------------------------------------

    [Fact]
    public void ProgramCs_ElseIfBranch_CallsDbSeederSeedAsync()
    {
        // DbSeeder.SeedAsync already appears once (in the IsDevelopment block). The new
        // else-if branch must introduce a second call — otherwise the Production path
        // would never seed the admin. Count must be >= 2.
        var occurrences = CountOccurrences(ProgramContent, "DbSeeder.SeedAsync");
        occurrences.Should().BeGreaterThanOrEqualTo(
            2,
            "DbSeeder.SeedAsync must appear in BOTH the IsDevelopment block AND the new else-if branch");
    }

    // -------------------------------------------------------------------------
    // 4b. Inside the else-if: db.Database.Migrate / MigrateAsync must NOT appear
    //     (migrations in Production are out-of-band)
    // -------------------------------------------------------------------------

    [Fact]
    public void ProgramCs_ElseIfBranch_DoesNotCallMigrate()
    {
        // Migrations in Production are handled out-of-band; the flag-gated branch
        // must only call SeedAsync, not Migrate/MigrateAsync.
        // Strategy: verify that MigrateAsync does NOT appear after the else-if but
        // before the closing of that block. We check the structural contract by
        // confirming SeedAsync is present (from test above) and MigrateAsync only
        // appears in the IsDevelopment block — i.e. the code between the SeedAsync
        // call in the else-if and the final else must not contain MigrateAsync.
        //
        // Simpler proxy: the code contains only one occurrence of MigrateAsync
        // (inside the IsDevelopment block).
        var occurrences = CountOccurrences(ProgramContent, "MigrateAsync");
        occurrences.Should().Be(
            1,
            "MigrateAsync should appear exactly once — inside the IsDevelopment block — " +
            "and never inside the else-if Production seed branch");
    }

    // -------------------------------------------------------------------------
    // 5. Log message "ForceAdminPasswordReset executed"
    // -------------------------------------------------------------------------

    [Fact]
    public void ProgramCs_ElseIfBranch_LogsForceAdminPasswordResetExecuted()
    {
        ProgramContent.Should().Contain(
            "ForceAdminPasswordReset executed",
            "the else-if branch must emit a LogInformation message: 'ForceAdminPasswordReset executed'");
    }

    // -------------------------------------------------------------------------
    // 6. The Skipping else branch is preserved
    // -------------------------------------------------------------------------

    [Fact]
    public void ProgramCs_PreservesSkippingElseBranch()
    {
        ProgramContent.Should().Contain(
            "Skipping automatic migrations",
            "the final else branch must still emit the 'Skipping automatic migrations' message");
    }

    // -------------------------------------------------------------------------
    // 7. AdminPassword value is never logged
    // -------------------------------------------------------------------------

    [Fact]
    public void ProgramCs_AdminPasswordValueIsNeverInterpolatedIntoLogMessages()
    {
        // We can't assert at runtime what value is logged, but we can statically
        // verify that no log call includes the AdminPassword config value in a way
        // that would expose it. The simplest static check: the Seed:AdminPassword
        // config key itself does not appear inside any LogInformation/Log.Information
        // argument that would render it as a message (i.e. it's only inside the
        // IsNullOrWhiteSpace guard, not inside a log template).
        //
        // Assert: the string "Seed:AdminPassword" appears in the file but only in
        // non-log contexts. Specifically it must NOT appear on the same logical line
        // as LogInformation or Log.Information.
        var lines = ProgramContent.Split('\n');
        var passwordLoggedOnSameLine = lines.Any(line =>
            line.Contains("Seed:AdminPassword", StringComparison.Ordinal) &&
            (line.Contains("LogInformation", StringComparison.Ordinal) ||
             line.Contains("Log.Information", StringComparison.Ordinal)));

        passwordLoggedOnSameLine.Should().BeFalse(
            "the value of Seed:AdminPassword must never appear in a log message — " +
            "no log call should reference the config key on the same line");
    }

    // -------------------------------------------------------------------------
    // 8. Structural: IsDevelopment block is preserved
    // -------------------------------------------------------------------------

    [Fact]
    public void ProgramCs_PreservesIsDevelopmentBlock()
    {
        ProgramContent.Should().Contain(
            "app.Environment.IsDevelopment()",
            "the existing IsDevelopment() guard must be preserved exactly");
    }

    [Fact]
    public void ProgramCs_IsDevelopmentBlock_StillCallsMigrate()
    {
        // The IsDevelopment block must still call MigrateAsync (unchanged)
        ProgramContent.Should().Contain(
            "MigrateAsync",
            "the IsDevelopment block must still contain the existing MigrateAsync() call");
    }

    [Fact]
    public void ProgramCs_IsDevelopmentBlock_StillCallsSeedAsync()
    {
        // DbSeeder.SeedAsync is called from at least the IsDevelopment block (and also
        // from the new else-if). The count must be >= 2.
        var occurrences = CountOccurrences(ProgramContent, "DbSeeder.SeedAsync");
        occurrences.Should().BeGreaterThanOrEqualTo(
            2,
            "DbSeeder.SeedAsync must appear in both the IsDevelopment block and the new else-if branch");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
