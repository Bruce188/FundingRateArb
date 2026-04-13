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
using Microsoft.Extensions.Caching.Memory;

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
