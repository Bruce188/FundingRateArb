using System.Reflection;
using Microsoft.Data.SqlClient;

namespace FundingRateArb.Tests.Unit.Helpers;

/// <summary>
/// Creates <see cref="SqlException"/> instances with a specific error number via
/// reflection, because <see cref="SqlException"/> has no public constructor.
/// </summary>
internal static class SqlExceptionFactory
{
    private static readonly Type SqlErrorCollectionType =
        typeof(SqlException).Assembly.GetType("Microsoft.Data.SqlClient.SqlErrorCollection")!;

    private static readonly Type SqlErrorType =
        typeof(SqlException).Assembly.GetType("Microsoft.Data.SqlClient.SqlError")!;

    private static readonly ConstructorInfo SqlErrorCollectionCtor =
        SqlErrorCollectionType.GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            null, Type.EmptyTypes, null)!;

    // SqlError(int infoNumber, byte errorState, byte errorClass, string server,
    //          string errorMessage, string procedure, int lineNumber, Exception exception)
    private static readonly ConstructorInfo SqlErrorCtor =
        SqlErrorType.GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            new[] { typeof(int), typeof(byte), typeof(byte), typeof(string), typeof(string), typeof(string), typeof(int), typeof(Exception) },
            null)!;

    private static readonly MethodInfo SqlErrorCollectionAdd =
        SqlErrorCollectionType.GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!;

    private static readonly MethodInfo CreateException =
        typeof(SqlException).GetMethod(
            "CreateException",
            BindingFlags.NonPublic | BindingFlags.Static,
            null,
            new[] { SqlErrorCollectionType, typeof(string) },
            null)!;

    /// <summary>
    /// Creates a <see cref="SqlException"/> with the specified error number.
    /// Use <c>10928</c> for a transient (Azure resource-limit) error,
    /// or <c>99999</c> for a non-transient error.
    /// </summary>
    public static SqlException Create(int errorNumber)
    {
        // Build a SqlErrorCollection with one SqlError carrying the desired Number.
        var collection = SqlErrorCollectionCtor.Invoke(null);

        var error = SqlErrorCtor.Invoke(new object?[]
        {
            errorNumber,   // infoNumber
            (byte)0,       // errorState
            (byte)16,      // errorClass (severity)
            "server",      // server
            "Test SQL error", // errorMessage
            string.Empty,  // procedure
            0,             // lineNumber
            null           // inner exception
        });

        SqlErrorCollectionAdd.Invoke(collection, new[] { error });

        return (SqlException)CreateException.Invoke(null, new[] { collection, "7.0.0" })!;
    }
}
