using FluentAssertions;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Tests.Integration.Repositories;

/// <summary>
/// DB-backed integration tests for
/// <see cref="FundingRateArb.Infrastructure.Repositories.UserExchangeCredentialRepository.GetDistinctUserIdsByExchangeNameAsync"/>.
///
/// Uses the shared <see cref="TestDbFixture"/> (in-memory EF Core) and seeds data
/// directly via the fixture's <c>DbContext</c>. No changes to the production repository.
/// </summary>
public class UserExchangeCredentialRepositoryTests : IDisposable
{
    private readonly TestDbFixture _fixture;

    // A dedicated exchange for dYdX-specific tests (separate from TestExchange so
    // seeded data in one test does not bleed across the shared TestExchange counters).
    private readonly Exchange _dydxExchange;

    public UserExchangeCredentialRepositoryTests()
    {
        _fixture = new TestDbFixture();

        _dydxExchange = new Exchange
        {
            Name = "dYdX",
            ApiBaseUrl = "https://indexer.dydx.trade",
            WsBaseUrl = "wss://indexer.dydx.trade/v4/ws",
            FundingInterval = FundingInterval.Hourly,
            FundingIntervalHours = 8,
            IsActive = true
        };
        _fixture.Context.Exchanges.Add(_dydxExchange);
        // review-v236: NB-8 — synchronous SaveChanges in ctor is intentional: xUnit does not
        // support async constructors, and IAsyncLifetime.InitializeAsync would require restructuring
        // the class layout significantly for a single-entity seed. The ctor seeds only the
        // Exchange entity (not UserExchangeCredential rows); per-test credential seeds use the
        // async SeedCredentialAsync helper below, keeping async-pattern consistency where possible.
        _fixture.Context.SaveChanges();
    }

    public void Dispose() => _fixture.Dispose();

    // ── Helpers ─────────────────────────────────────────────────────────────────

    // review-v230: NB10 — async seed helper to match the production async access pattern
    private async Task SeedCredentialAsync(string userId, Exchange exchange, bool isActive = true)
    {
        _fixture.Context.UserExchangeCredentials.Add(new UserExchangeCredential
        {
            UserId = userId,
            ExchangeId = exchange.Id,
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow
        });
        await _fixture.Context.SaveChangesAsync();  // review-v230: NB10
    }

    // ── Tests ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDistinctUserIdsByExchangeNameAsync_EmptyTable_ReturnsEmptyList()
    {
        // Arrange — no credentials seeded for dYdX

        // Act
        var result = await _fixture.UnitOfWork.UserCredentials
            .GetDistinctUserIdsByExchangeNameAsync("dYdX");

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDistinctUserIdsByExchangeNameAsync_MultipleActiveCredentialsForSameUser_ReturnsSingleEntry()
    {
        // Arrange — same (userId, exchangeName) pair appears twice (e.g., credential refresh)
        await SeedCredentialAsync("user-dedup", _dydxExchange);   // review-v230: NB10
        await SeedCredentialAsync("user-dedup", _dydxExchange);   // duplicate; review-v230: NB10

        // Act
        var result = await _fixture.UnitOfWork.UserCredentials
            .GetDistinctUserIdsByExchangeNameAsync("dYdX");

        // Assert — deduplicated to one entry
        result.Should().ContainSingle()
            .Which.Should().Be("user-dedup");
    }

    [Fact]
    public async Task GetDistinctUserIdsByExchangeNameAsync_InactiveCredential_ExcludedFromResults()
    {
        // Arrange — active and inactive credentials for different user IDs
        await SeedCredentialAsync("user-active", _dydxExchange, isActive: true);    // review-v230: NB10
        await SeedCredentialAsync("user-inactive", _dydxExchange, isActive: false); // review-v230: NB10

        // Act
        var result = await _fixture.UnitOfWork.UserCredentials
            .GetDistinctUserIdsByExchangeNameAsync("dYdX");

        // Assert — only the active user appears
        result.Should().ContainSingle()
            .Which.Should().Be("user-active");
        result.Should().NotContain("user-inactive");
    }

    [Fact]
    public async Task GetDistinctUserIdsByExchangeNameAsync_DifferentExchange_ExcludedFromResults()
    {
        // Arrange — one dYdX credential and one credential for a different exchange
        await SeedCredentialAsync("user-dydx", _dydxExchange);           // review-v230: NB10
        await SeedCredentialAsync("user-other", _fixture.TestExchange);  // different exchange; review-v230: NB10

        // Act
        var result = await _fixture.UnitOfWork.UserCredentials
            .GetDistinctUserIdsByExchangeNameAsync("dYdX");

        // Assert — only the dYdX user appears
        result.Should().ContainSingle()
            .Which.Should().Be("user-dydx");
    }

    // review-v230: NB7 — renamed to make the in-memory-only caveat visible in the test name;
    // decorated with [Trait("Caveat","InMemoryOnly")] so readers running this test in isolation
    // understand this documents in-memory EF behaviour, not SQL Server production semantics.
    [Fact]
    [Trait("Caveat", "InMemoryOnly")]
    public async Task GetDistinctUserIdsByExchangeNameAsync_ExchangeNameCaseSensitivity_PinsInMemoryProviderBehaviour_NotProductionSemantics()
    {
        // Arrange — credential stored under exchange named "dYdX" (mixed case)
        await SeedCredentialAsync("user-case", _dydxExchange); // review-v230: NB10

        // Act — query with exact case and with lower-case variant
        var exactMatch = await _fixture.UnitOfWork.UserCredentials
            .GetDistinctUserIdsByExchangeNameAsync("dYdX");

        var lowerCaseMatch = await _fixture.UnitOfWork.UserCredentials
            .GetDistinctUserIdsByExchangeNameAsync("dydx");

        // Assert — pins the OBSERVED behaviour of the in-memory EF Core provider.
        // The in-memory provider uses ordinal string comparison (case-sensitive by default),
        // so "dydx" does NOT match the stored "dYdX" name in tests.
        // On SQL Server with the default CI_AS collation, "dydx" WOULD match — so this
        // test documents a known test/production collation difference.
        // If either assertion fails, the underlying provider or query semantics changed.
        exactMatch.Should().ContainSingle(
            because: "querying with the exact stored name 'dYdX' must return the credential");
        lowerCaseMatch.Should().BeEmpty(
            because: "the in-memory EF provider is case-sensitive: 'dydx' does not match 'dYdX' " +
                     "(note: SQL Server CI_AS collation would match — this is a known divergence)");
    }

    // review-v230: NB1 — NEW test closing plan Task 2.2 acceptance criterion #5.
    // EF Core 8's in-memory provider honours CancellationToken on ToListAsync,
    // so OperationCanceledException is the expected throw — no provider-specific workaround required.
    //
    // review-v236: NB-4 — fixed casing from "dydx" to "dYdX" to match stored exchange name;
    // seeded one active credential so EF Core's in-memory provider has data to cancel against,
    // preventing a vacuous empty-result short-circuit before the cancellation check.
    [Fact]
    [Trait("Caveat", "InMemoryOnly")]  // review-v236: NB-4 — consistent with the case-sensitivity test
    public async Task GetDistinctUserIdsByExchangeNameAsync_PreCancelledToken_ThrowsOperationCanceledException()
    {
        // Arrange — seed one active dYdX credential so EF Core evaluates the query
        // (in-memory provider may short-circuit an empty-table scan before checking cancellation)
        await SeedCredentialAsync("user-cancel-test", _dydxExchange); // review-v236: NB-4

        using var cts = new CancellationTokenSource(); // review-v239: N3 — dispose IDisposable CTS
        cts.Cancel(); // review-v230: NB1 — pre-cancel before invocation

        // Act & Assert — "dYdX" matches the stored exchange name (review-v236: NB-4)
        await FluentActions
            .Invoking(() => _fixture.UnitOfWork.UserCredentials
                .GetDistinctUserIdsByExchangeNameAsync("dYdX", cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }
}
