using System.Linq.Expressions;
using FluentAssertions;
using FundingRateArb.Application.Common;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Infrastructure.Repositories;
using FundingRateArb.Tests.Unit.Helpers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Tests.Unit.Repositories;

public class FundingRateRepositoryTests
{
    // ── NT5: FundingRateRepository exception path tests ──────────────────────

    [Fact]
    public async Task GetLatestPerExchangePerAssetAsync_TransientSqlException_ThrowsDatabaseUnavailable()
    {
        // Arrange — context whose FundingRateSnapshots throws a transient SqlException on iteration
        var transientEx = SqlExceptionFactory.Create(10928);
        var context = CreateThrowingContext(transientEx);
        var sut = new FundingRateRepository(context, new MemoryCache(new MemoryCacheOptions()));

        // Act & Assert
        await sut.Invoking(r => r.GetLatestPerExchangePerAssetAsync())
            .Should().ThrowAsync<DatabaseUnavailableException>(
                "transient SqlException (10928) must be wrapped as DatabaseUnavailableException");
    }

    [Fact]
    public async Task GetLatestPerExchangePerAssetAsync_NonTransientSqlException_PropagatesSqlException()
    {
        // Arrange — context throws a non-transient SqlException (99999 not in allowlist)
        var nonTransientEx = SqlExceptionFactory.Create(99999);
        var context = CreateThrowingContext(nonTransientEx);
        var sut = new FundingRateRepository(context, new MemoryCache(new MemoryCacheOptions()));

        // Act & Assert — non-transient SqlException must propagate unchanged (not wrapped)
        var thrown = await sut.Invoking(r => r.GetLatestPerExchangePerAssetAsync())
            .Should().ThrowAsync<SqlException>();
        thrown.Which.Number.Should().Be(99999);
    }

    [Fact]
    public async Task GetLatestPerExchangePerAssetAsync_InternalTimeout_ThrowsDatabaseUnavailable()
    {
        // Arrange — context that stalls longer than the repository's 20s internal CTS
        var context = CreateStallingContext(delaySeconds: 25);
        var sut = new FundingRateRepository(context, new MemoryCache(new MemoryCacheOptions()));

        // Act — internal 20s CTS fires before the 25s stall completes.
        // Use a 22s outer timeout so the test does not hang if the internal CTS misfires.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(22));
        var task = sut.GetLatestPerExchangePerAssetAsync();
        var completedTask = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, timeoutCts.Token));
        completedTask.Should().Be(task, "repository must complete within 22s via its internal 20s CTS");

        // Assert — unwrap the completed task and verify the exception type
        var act = async () => await task;
        await act.Should().ThrowAsync<DatabaseUnavailableException>(
            "timeout from internal 20s CTS must surface as DatabaseUnavailableException");
    }

    // ── Cooldown + suppression tests ─────────────────────────────────────────

    [Fact]
    public async Task PurgeOlderThanAsync_WhenRetryLimitExceeded_LogsWarningOnceAndSetsCooldown()
    {
        // Arrange — context whose ExecuteDeleteAsync throws RetryLimitExceededException
        var retryEx = new RetryLimitExceededException("retry limit exceeded", new Exception("inner"));
        var context = CreateThrowingContext(retryEx);
        var logEntries = new List<(LogLevel Level, string Message, Exception? Ex)>();
        var logger = new CapturingLogger<FundingRateRepository>(logEntries);
        var repository = new FundingRateRepository(context, new MemoryCache(new MemoryCacheOptions()), logger);

        // Act — first call: should throw and set cooldown
        var act = async () => await repository.PurgeOlderThanAsync(DateTime.UtcNow, ct: CancellationToken.None);
        await act.Should().ThrowAsync<RetryLimitExceededException>(
            "RetryLimitExceededException must be rethrown after catch");

        // Assert — exactly one Warning log entry
        var warnings = logEntries.Where(e => e.Level == LogLevel.Warning).ToList();
        warnings.Should().HaveCount(1, "warning must be emitted exactly once");
        warnings[0].Ex.Should().Be(retryEx, "the caught exception must be attached to the log entry");

        // Assert — subsequent call short-circuits (cooldown is active)
        var result = await repository.PurgeOlderThanAsync(DateTime.UtcNow, ct: CancellationToken.None);
        result.Should().Be(0, "call during cooldown must short-circuit and return 0");
        repository.SuppressedPurgeCount.Should().Be(1,
            "suppressed counter must be incremented on the short-circuited call");
    }

    [Fact]
    public async Task PurgeOlderThanAsync_DuringCooldown_ShortCircuitsAndIncrementsSuppressedCount()
    {
        // Arrange — prime the cooldown by triggering a RetryLimitExceededException first
        var retryEx = new RetryLimitExceededException("retry limit exceeded", new Exception("inner"));
        var throwingContext = CreateThrowingContext(retryEx);
        var repository = new FundingRateRepository(throwingContext, new MemoryCache(new MemoryCacheOptions()));

        // Prime cooldown
        try { await repository.PurgeOlderThanAsync(DateTime.UtcNow, ct: CancellationToken.None); }
        catch (RetryLimitExceededException) { /* expected — primes cooldown */ }

        // Act — second call during active cooldown (throwing context would throw if reached)
        var result = await repository.PurgeOlderThanAsync(DateTime.UtcNow, ct: CancellationToken.None);

        // Assert — short-circuit: returns 0, does NOT reach ExecuteDeleteAsync (no throw), counter incremented
        result.Should().Be(0, "call during cooldown must return 0 without invoking ExecuteDeleteAsync");
        repository.SuppressedPurgeCount.Should().Be(1,
            "suppressed counter must be exactly 1 after one short-circuited call");
    }

    [Fact]
    public async Task PurgeOlderThanAsync_AfterCooldownExpiry_ResumesNormalExecution()
    {
        // Arrange — context that returns a fixed delete count (simulates successful bulk delete)
        const int expectedDeleted = 2;
        var context = CreateSucceedingContext(expectedDeleted);
        var repository = new FundingRateRepository(context, new MemoryCache(new MemoryCacheOptions()));

        // Set cooldown to a past timestamp (simulates an already-expired cooldown)
        repository.PurgeRetryCooldownUntil = DateTimeOffset.UtcNow.AddMinutes(-1);

        // Act
        var deletedCount = await repository.PurgeOlderThanAsync(DateTime.UtcNow, ct: CancellationToken.None);

        // Assert — delete proceeded (not short-circuited), cooldown cleared
        deletedCount.Should().Be(expectedDeleted,
            "expired cooldown must not prevent the delete from executing");
        repository.PurgeRetryCooldownUntil.Should().BeNull(
            "expired cooldown must be cleared after normal execution resumes");
        repository.SuppressedPurgeCount.Should().Be(0,
            "no suppression occurred — this was a normal post-expiry call");
    }

    // ── Force override tests ─────────────────────────────────────────────────

    [Fact]
    public async Task PurgeOlderThanAsync_WithForceTrue_AndSuppressedCountZero_DoesNotBypassCooldown()
    {
        // Arrange — a repository whose cooldown is active but suppressedCount == 0
        var context = CreateSucceedingContext(5);
        var repository = new FundingRateRepository(context, new MemoryCache(new MemoryCacheOptions()));

        // Set an active cooldown without ever having a suppressed call (counter stays 0)
        repository.PurgeRetryCooldownUntil = DateTimeOffset.UtcNow.AddMinutes(10);

        // Act — force=true but counter is 0 → bypassCooldown = false → cooldown still guards
        var result = await repository.PurgeOlderThanAsync(DateTime.UtcNow, force: true, ct: CancellationToken.None);

        // Assert — short-circuited (cooldown still active), suppressed counter incremented
        result.Should().Be(0, "force has no effect when suppressed counter is 0");
        repository.SuppressedPurgeCount.Should().Be(1,
            "cooldown was active so the call was suppressed and counter incremented");
    }

    [Fact]
    public async Task PurgeOlderThanAsync_WithForceTrue_AndSuppressedCountPositive_BypassesCooldown()
    {
        // Arrange — prime counter > 0 by triggering a RetryLimitExceededException
        var retryEx = new RetryLimitExceededException("retry limit exceeded", new Exception("inner"));
        var throwingContext = CreateThrowingContext(retryEx);
        var throwingRepository = new FundingRateRepository(throwingContext, new MemoryCache(new MemoryCacheOptions()));

        try { await throwingRepository.PurgeOlderThanAsync(DateTime.UtcNow, ct: CancellationToken.None); }
        catch (RetryLimitExceededException) { /* primes cooldown */ }

        // Suppress a call to make counter > 0
        await throwingRepository.PurgeOlderThanAsync(DateTime.UtcNow, ct: CancellationToken.None);

        // Now swap to a succeeding context by creating a fresh repo with the counter pre-set
        var succeedingContext = CreateSucceedingContext(10);
        var repository = new FundingRateRepository(succeedingContext, new MemoryCache(new MemoryCacheOptions()));

        // Simulate active cooldown + suppressed count > 0
        repository.PurgeRetryCooldownUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        // We access the internal counter via SuppressedPurgeCount — set it by doing a suppressed call
        // Since this new repo has no cooldown, prime it:
        // Set cooldown first then do a suppressed call to increment counter
        repository.PurgeRetryCooldownUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        await repository.PurgeOlderThanAsync(DateTime.UtcNow, ct: CancellationToken.None); // increments suppressed count
        repository.SuppressedPurgeCount.Should().Be(1, "counter should be 1 after one suppressed call");

        // Act — force=true with counter > 0 inside cooldown window → should execute the purge
        var result = await repository.PurgeOlderThanAsync(DateTime.UtcNow, force: true, ct: CancellationToken.None);

        // Assert — purge ran (returned 10 from succeeding context)
        result.Should().Be(10, "force=true with counter > 0 must bypass cooldown and execute purge");
    }

    [Fact]
    public async Task PurgeOlderThanAsync_WithForceTrue_ResetsSuppressedCountAfterSuccess()
    {
        // Arrange — repository with active cooldown and suppressed count > 0
        var succeedingContext = CreateSucceedingContext(3);
        var repository = new FundingRateRepository(succeedingContext, new MemoryCache(new MemoryCacheOptions()));

        repository.PurgeRetryCooldownUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        await repository.PurgeOlderThanAsync(DateTime.UtcNow, ct: CancellationToken.None); // prime counter
        repository.SuppressedPurgeCount.Should().Be(1, "pre-condition: counter == 1");

        // Act — forced purge succeeds
        var result = await repository.PurgeOlderThanAsync(DateTime.UtcNow, force: true, ct: CancellationToken.None);

        // Assert — counter reset to 0 after successful forced purge
        result.Should().Be(3, "forced purge must execute");
        repository.SuppressedPurgeCount.Should().Be(0,
            "counter must be reset to 0 after a successful forced purge");
    }

    // ── N3: bypass-success cooldown cleared, not re-armed ────────────────────

    [Fact]
    public async Task PurgeOlderThanAsync_BypassCooldown_Success_ClearsCooldown_NotRearms()
    {
        // Arrange: pre-set cooldown to future, suppressedCount > 0 (needed for bypassCooldown=true)
        var succeedingContext = CreateSucceedingContext(5);
        var repository = new FundingRateRepository(succeedingContext, new MemoryCache(new MemoryCacheOptions()));

        // Prime: active cooldown + suppressed count > 0
        repository.PurgeRetryCooldownUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        await repository.PurgeOlderThanAsync(DateTime.UtcNow, ct: CancellationToken.None); // increments suppressed count
        repository.SuppressedPurgeCount.Should().Be(1, "pre-condition: one suppressed call");
        repository.PurgeRetryCooldownUntil.Should().NotBeNull("pre-condition: cooldown is active");

        // Act: bypass (force=true, suppressedCount=1 > 0) → purge succeeds
        var result = await repository.PurgeOlderThanAsync(DateTime.UtcNow, force: true, ct: CancellationToken.None);

        // Assert: cooldown is cleared (null), NOT re-armed to a future time
        result.Should().Be(5, "forced purge must execute");
        repository.PurgeRetryCooldownUntil.Should().BeNull(
            "bypass success must clear the cooldown, not re-arm it");

        // Assert: a subsequent non-bypass call is not blocked
        var followUp = await repository.PurgeOlderThanAsync(DateTime.UtcNow, force: false, ct: CancellationToken.None);
        followUp.Should().Be(5, "after bypass success, non-bypass call must not be blocked by cooldown");
    }

    [Fact]
    public async Task PurgeOlderThanAsync_BypassCooldown_ThrowsRetryLimit_ArmsCooldown()
    {
        // Arrange: force path throws RetryLimitExceededException → 15-min cooldown must still be armed
        var retryEx = new RetryLimitExceededException("retry limit exceeded", new Exception("inner"));
        var throwingContext = CreateThrowingContext(retryEx);
        var repository = new FundingRateRepository(throwingContext, new MemoryCache(new MemoryCacheOptions()));

        // Prime suppressedCount > 0 by setting cooldown and doing a suppressed call
        repository.PurgeRetryCooldownUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        await repository.PurgeOlderThanAsync(DateTime.UtcNow, ct: CancellationToken.None);
        repository.SuppressedPurgeCount.Should().Be(1, "pre-condition: one suppressed call");

        // Act: bypass force call that throws RetryLimitExceededException
        var act = async () => await repository.PurgeOlderThanAsync(DateTime.UtcNow, force: true, ct: CancellationToken.None);
        await act.Should().ThrowAsync<RetryLimitExceededException>(
            "RetryLimitExceededException must propagate from force path");

        // Assert: 15-min cooldown is armed (safety net preserved)
        repository.PurgeRetryCooldownUntil.Should().NotBeNull(
            "RetryLimitExceededException on force path must arm the 15-min cooldown");
        repository.PurgeRetryCooldownUntil!.Value.Should().BeCloseTo(
            DateTimeOffset.UtcNow.AddMinutes(15), TimeSpan.FromSeconds(5),
            "cooldown must be set to approximately 15 minutes from now");
    }

    [Fact]
    public async Task PurgeOlderThanAsync_NonBypass_Success_PreservesBehavior()
    {
        // Arrange: no cooldown active, suppressedCount = 0 → normal non-bypass path
        const int expectedDeleted = 7;
        var context = CreateSucceedingContext(expectedDeleted);
        var repository = new FundingRateRepository(context, new MemoryCache(new MemoryCacheOptions()));

        // Act
        var result = await repository.PurgeOlderThanAsync(DateTime.UtcNow, force: false, ct: CancellationToken.None);

        // Assert: purge runs, no cooldown armed, suppressed count unchanged
        result.Should().Be(expectedDeleted, "non-bypass success must return the delete count");
        repository.PurgeRetryCooldownUntil.Should().BeNull(
            "non-bypass success must not arm a cooldown");
        repository.SuppressedPurgeCount.Should().Be(0,
            "non-bypass success must not modify the suppressed count");
    }

    [Fact]
    public async Task GetSuppressedPurgeCount_ReturnsCurrentCounter()
    {
        // Arrange — prime the cooldown
        var retryEx = new RetryLimitExceededException("retry limit exceeded", new Exception("inner"));
        var context = CreateThrowingContext(retryEx);
        var repository = new FundingRateRepository(context, new MemoryCache(new MemoryCacheOptions()));

        try { await repository.PurgeOlderThanAsync(DateTime.UtcNow, ct: CancellationToken.None); }
        catch (RetryLimitExceededException) { /* expected */ }

        // Make one suppressed call to increment the counter
        await repository.PurgeOlderThanAsync(DateTime.UtcNow, ct: CancellationToken.None);

        // Assert — GetSuppressedPurgeCount round-trips through the interface
        repository.GetSuppressedPurgeCount().Should().Be(1,
            "GetSuppressedPurgeCount must return the current suppression counter value");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static AppDbContext CreateThrowingContext(Exception exceptionToThrow)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ThrowingFundingRateContext(options, exceptionToThrow);
    }

    private static AppDbContext CreateStallingContext(int delaySeconds)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new StallingFundingRateContext(options, delaySeconds);
    }

    private static AppDbContext CreateSucceedingContext(int rowCount)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SucceedingFundingRateContext(options, rowCount);
    }
}

// ── SucceedingDbSet<T> ────────────────────────────────────────────────────────

internal sealed class SucceedingDbSet<T> : DbSet<T>, IQueryable<T>, IAsyncEnumerable<T>
    where T : class
{
    private readonly int _rowCount;
    private readonly IQueryable<T> _empty;

    public SucceedingDbSet(int rowCount)
    {
        _rowCount = rowCount;
        _empty = Enumerable.Empty<T>().AsQueryable();
    }

    public override IEntityType EntityType
        => throw new NotSupportedException("EntityType not supported in SucceedingDbSet");

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => _empty.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _empty.GetEnumerator();
    Type IQueryable.ElementType => typeof(T);
    Expression IQueryable.Expression => _empty.Expression;
    IQueryProvider IQueryable.Provider => new SucceedingAsyncQueryProvider(_rowCount);

    public new IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default)
        => new EmptyAsyncEnumerator<T>();
}

/// <summary>
/// <see cref="IAsyncQueryProvider"/> that returns a successfully-completed <see cref="Task{int}"/>
/// with a pre-configured row count, simulating a successful <c>ExecuteDeleteAsync</c>.
/// </summary>
internal sealed class SucceedingAsyncQueryProvider : IAsyncQueryProvider
{
    private readonly int _rowCount;

    public SucceedingAsyncQueryProvider(int rowCount) => _rowCount = rowCount;

    public IQueryable CreateQuery(Expression expression)
        => new SucceedingEnumerable<object>(expression, _rowCount);

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
        => new SucceedingEnumerable<TElement>(expression, _rowCount);

    public object? Execute(Expression expression) => _rowCount;

    public TResult Execute<TResult>(Expression expression) => default!;

    public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken ct = default)
    {
        // TResult is Task<int> for ExecuteDeleteAsync
        if (typeof(TResult) == typeof(Task<int>))
        {
            return (TResult)(object)Task.FromResult(_rowCount);
        }

        var resultElementType = typeof(TResult).GetGenericArguments()[0];
        var method = typeof(SucceedingAsyncQueryProvider)
            .GetMethod(nameof(MakeSucceedingTask), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .MakeGenericMethod(resultElementType);
        return (TResult)method.Invoke(this, null)!;
    }

    private Task<TElement> MakeSucceedingTask<TElement>() => Task.FromResult(default(TElement)!);
}

internal sealed class SucceedingEnumerable<T> : IQueryable<T>, IAsyncEnumerable<T>
{
    private readonly int _rowCount;

    public SucceedingEnumerable(Expression expression, int rowCount)
    {
        Expression = expression;
        _rowCount = rowCount;
    }

    public Type ElementType => typeof(T);
    public Expression Expression { get; }
    public IQueryProvider Provider => new SucceedingAsyncQueryProvider(_rowCount);

    public IEnumerator<T> GetEnumerator() => Enumerable.Empty<T>().GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => Enumerable.Empty<T>().GetEnumerator();

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default)
        => new EmptyAsyncEnumerator<T>();
}

// ── EmptyAsyncEnumerator<T> ───────────────────────────────────────────────────

internal sealed class EmptyAsyncEnumerator<T> : IAsyncEnumerator<T>
{
    public T Current => default!;
    public ValueTask<bool> MoveNextAsync() => new(false);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// ── CapturingLogger<T> ───────────────────────────────────────────────────────

internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message, Exception? Ex)> _entries;

    public CapturingLogger(List<(LogLevel Level, string Message, Exception? Ex)> entries)
        => _entries = entries;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _entries.Add((logLevel, formatter(state, exception), exception));
    }
}

// ── Context subclasses ───────────────────────────────────────────────────────

internal sealed class ThrowingFundingRateContext : AppDbContext
{
    private readonly Exception _exceptionToThrow;

    public ThrowingFundingRateContext(DbContextOptions<AppDbContext> options, Exception exceptionToThrow)
        : base(options) => _exceptionToThrow = exceptionToThrow;

    public override DbSet<T> Set<T>()
        where T : class
    {
        if (typeof(T) == typeof(FundingRateSnapshot))
        {
            return (DbSet<T>)(object)new ThrowingDbSet<FundingRateSnapshot>(_exceptionToThrow);
        }

        return base.Set<T>();
    }
}

internal sealed class StallingFundingRateContext : AppDbContext
{
    private readonly int _delaySeconds;

    public StallingFundingRateContext(DbContextOptions<AppDbContext> options, int delaySeconds)
        : base(options) => _delaySeconds = delaySeconds;

    public override DbSet<T> Set<T>()
        where T : class
    {
        if (typeof(T) == typeof(FundingRateSnapshot))
        {
            return (DbSet<T>)(object)new StallingDbSet<FundingRateSnapshot>(_delaySeconds);
        }

        return base.Set<T>();
    }
}

internal sealed class SucceedingFundingRateContext : AppDbContext
{
    private readonly int _rowCount;

    public SucceedingFundingRateContext(DbContextOptions<AppDbContext> options, int rowCount)
        : base(options) => _rowCount = rowCount;

    public override DbSet<T> Set<T>()
        where T : class
    {
        if (typeof(T) == typeof(FundingRateSnapshot))
        {
            return (DbSet<T>)(object)new SucceedingDbSet<FundingRateSnapshot>(_rowCount);
        }

        return base.Set<T>();
    }
}

// ── ThrowingDbSet<T> ──────────────────────────────────────────────────────────

internal sealed class ThrowingDbSet<T> : DbSet<T>, IQueryable<T>, IAsyncEnumerable<T>
    where T : class
{
    private readonly Exception _exceptionToThrow;
    private readonly IQueryable<T> _empty;

    public ThrowingDbSet(Exception exceptionToThrow)
    {
        _exceptionToThrow = exceptionToThrow;
        _empty = Enumerable.Empty<T>().AsQueryable();
    }

    // DbSet<T> requires EntityType — return null-object implementation.
    public override IEntityType EntityType
        => throw new NotSupportedException("EntityType not supported in ThrowingDbSet");

    // IQueryable members — use a throwing async provider so ToListAsync throws.
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => _empty.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _empty.GetEnumerator();
    Type IQueryable.ElementType => typeof(T);
    Expression IQueryable.Expression => _empty.Expression;
    IQueryProvider IQueryable.Provider => new ThrowingAsyncQueryProvider(_exceptionToThrow);

    // IAsyncEnumerable — throws on first MoveNextAsync.
    public new IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default)
        => new ThrowingAsyncEnumerator<T>(_exceptionToThrow, ct);
}

// ── StallingDbSet<T> ──────────────────────────────────────────────────────────

internal sealed class StallingDbSet<T> : DbSet<T>, IQueryable<T>, IAsyncEnumerable<T>
    where T : class
{
    private readonly int _delaySeconds;
    private readonly IQueryable<T> _empty;

    public StallingDbSet(int delaySeconds)
    {
        _delaySeconds = delaySeconds;
        _empty = Enumerable.Empty<T>().AsQueryable();
    }

    public override IEntityType EntityType
        => throw new NotSupportedException("EntityType not supported in StallingDbSet");

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => _empty.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _empty.GetEnumerator();
    Type IQueryable.ElementType => typeof(T);
    Expression IQueryable.Expression => _empty.Expression;
    IQueryProvider IQueryable.Provider => new StallingAsyncQueryProvider(_delaySeconds);

    public new IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default)
        => new StallingAsyncEnumerator<T>(_delaySeconds, ct);
}

// ── Async query providers ─────────────────────────────────────────────────────

/// <summary>
/// <see cref="IAsyncQueryProvider"/> that returns a faulting <see cref="Task{T}"/> for every
/// <c>ExecuteAsync</c> call. Intermediate <c>CreateQuery&lt;TElement&gt;</c> calls return a
/// <see cref="ThrowingEnumerable{TElement}"/> whose provider is also this type, so the
/// throwing behaviour propagates through EF Core's LINQ composition pipeline.
/// </summary>
internal sealed class ThrowingAsyncQueryProvider : IAsyncQueryProvider
{
    private readonly Exception _exception;

    public ThrowingAsyncQueryProvider(Exception exception) => _exception = exception;

    public IQueryable CreateQuery(Expression expression)
        => new ThrowingEnumerable<object>(expression, _exception);

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
        => new ThrowingEnumerable<TElement>(expression, _exception);

    public object? Execute(Expression expression) => throw _exception;

    public TResult Execute<TResult>(Expression expression) => throw _exception;

    public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken ct = default)
    {
        // EF Core calls ExecuteAsync<Task<List<T>>> for ToListAsync.
        var resultElementType = typeof(TResult).GetGenericArguments()[0];
        var method = typeof(ThrowingAsyncQueryProvider)
            .GetMethod(nameof(MakeThrowingTask), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .MakeGenericMethod(resultElementType);
        return (TResult)method.Invoke(this, null)!;
    }

    private Task<TElement> MakeThrowingTask<TElement>()
        => Task.FromException<TElement>(_exception);
}

/// <summary>
/// <see cref="IAsyncQueryProvider"/> that stalls every <c>ExecuteAsync</c> call for
/// <paramref name="delaySeconds"/> seconds (respecting the <see cref="CancellationToken"/>),
/// allowing the repository's internal timeout CTS to fire first.
/// </summary>
internal sealed class StallingAsyncQueryProvider : IAsyncQueryProvider
{
    private readonly int _delaySeconds;

    public StallingAsyncQueryProvider(int delaySeconds) => _delaySeconds = delaySeconds;

    public IQueryable CreateQuery(Expression expression)
        => new StallingEnumerable<object>(expression, _delaySeconds);

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
        => new StallingEnumerable<TElement>(expression, _delaySeconds);

    public object? Execute(Expression expression) => Enumerable.Empty<object>();

    public TResult Execute<TResult>(Expression expression) => default!;

    public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken ct = default)
    {
        var resultElementType = typeof(TResult).GetGenericArguments()[0];
        var method = typeof(StallingAsyncQueryProvider)
            .GetMethod(nameof(MakeStallingTask), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .MakeGenericMethod(resultElementType);
        return (TResult)method.Invoke(this, new object[] { ct })!;
    }

    private async Task<TElement> MakeStallingTask<TElement>(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(_delaySeconds), ct);
        return default!;
    }
}

// ── Expression-backed async enumerables ──────────────────────────────────────

internal sealed class ThrowingEnumerable<T> : IQueryable<T>, IAsyncEnumerable<T>
{
    private readonly Exception _exception;

    public ThrowingEnumerable(Expression expression, Exception exception)
    {
        Expression = expression;
        _exception = exception;
    }

    public Type ElementType => typeof(T);
    public Expression Expression { get; }
    public IQueryProvider Provider => new ThrowingAsyncQueryProvider(_exception);

    public IEnumerator<T> GetEnumerator() => Enumerable.Empty<T>().GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => Enumerable.Empty<T>().GetEnumerator();

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default)
        => new ThrowingAsyncEnumerator<T>(_exception, ct);
}

internal sealed class StallingEnumerable<T> : IQueryable<T>, IAsyncEnumerable<T>
{
    private readonly int _delaySeconds;

    public StallingEnumerable(Expression expression, int delaySeconds)
    {
        Expression = expression;
        _delaySeconds = delaySeconds;
    }

    public Type ElementType => typeof(T);
    public Expression Expression { get; }
    public IQueryProvider Provider => new StallingAsyncQueryProvider(_delaySeconds);

    public IEnumerator<T> GetEnumerator() => Enumerable.Empty<T>().GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => Enumerable.Empty<T>().GetEnumerator();

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default)
        => new StallingAsyncEnumerator<T>(_delaySeconds, ct);
}

// ── Async enumerators ────────────────────────────────────────────────────────

internal sealed class ThrowingAsyncEnumerator<T> : IAsyncEnumerator<T>
{
    private readonly Exception _exception;

    public ThrowingAsyncEnumerator(Exception exception, CancellationToken ct)
        => _exception = exception;

    public T Current => default!;

    public ValueTask<bool> MoveNextAsync()
        => ValueTask.FromException<bool>(_exception);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class StallingAsyncEnumerator<T> : IAsyncEnumerator<T>
{
    private readonly int _delaySeconds;
    private readonly CancellationToken _ct;

    public StallingAsyncEnumerator(int delaySeconds, CancellationToken ct)
    {
        _delaySeconds = delaySeconds;
        _ct = ct;
    }

    public T Current => default!;

    public async ValueTask<bool> MoveNextAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(_delaySeconds), _ct);
        return false;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
