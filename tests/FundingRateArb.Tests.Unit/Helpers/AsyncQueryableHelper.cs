using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;

namespace FundingRateArb.Tests.Unit.Helpers;

/// <summary>
/// Wraps an in-memory list so that EF Core async LINQ extensions (ToListAsync, etc.)
/// work in unit tests without a real DbContext.
/// </summary>
public static class AsyncQueryableHelper
{
    public static IQueryable<T> AsAsyncQueryable<T>(this IEnumerable<T> source)
    {
        return new TestAsyncEnumerable<T>(source);
    }
}

internal sealed class TestAsyncEnumerable<T> : EnumerableQuery<T>, IAsyncEnumerable<T>, IQueryable<T>
{
    public TestAsyncEnumerable(IEnumerable<T> enumerable) : base(enumerable)
    {
    }

    public TestAsyncEnumerable(Expression expression) : base(expression)
    {
    }

    IQueryProvider IQueryable.Provider => new TestAsyncQueryProvider<T>(this);

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        return new TestAsyncEnumerator<T>(this.AsEnumerable().GetEnumerator());
    }
}

internal sealed class TestAsyncQueryProvider<TEntity> : IAsyncQueryProvider
{
    private readonly IQueryProvider _inner;

    public TestAsyncQueryProvider(IQueryProvider inner)
    {
        _inner = inner;
    }

    public IQueryable CreateQuery(Expression expression)
    {
        return new TestAsyncEnumerable<TEntity>(expression);
    }

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
    {
        return new TestAsyncEnumerable<TElement>(expression);
    }

    public object? Execute(Expression expression)
    {
        return _inner.Execute(expression);
    }

    public TResult Execute<TResult>(Expression expression)
    {
        return _inner.Execute<TResult>(expression);
    }

    public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
    {
        var expectedResultType = typeof(TResult).GetGenericArguments()[0];
        var executionResult = typeof(IQueryProvider)
            .GetMethod(nameof(IQueryProvider.Execute), 1, new[] { typeof(Expression) })!
            .MakeGenericMethod(expectedResultType)
            .Invoke(_inner, new object[] { expression });

        return (TResult)typeof(Task)
            .GetMethod(nameof(Task.FromResult))!
            .MakeGenericMethod(expectedResultType)
            .Invoke(null, new[] { executionResult })!;
    }
}

internal sealed class TestAsyncEnumerator<T> : IAsyncEnumerator<T>
{
    private readonly IEnumerator<T> _inner;

    public TestAsyncEnumerator(IEnumerator<T> inner)
    {
        _inner = inner;
    }

    public T Current => _inner.Current;

    public ValueTask<bool> MoveNextAsync()
    {
        return new ValueTask<bool>(_inner.MoveNext());
    }

    public ValueTask DisposeAsync()
    {
        _inner.Dispose();
        return ValueTask.CompletedTask;
    }
}
