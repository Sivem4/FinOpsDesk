using System.Data;
using System.Data.Common;
using System.Globalization;

using FinOpsDesk.Modules.Access.Application;

using Microsoft.Data.SqlClient;

namespace FinOpsDesk.Modules.Access.Infrastructure.Persistence;

// One resource name for every participant: finopsdesk-auth:{organizationId:D}.
// sp_getapplock is scoped by database id, principal, and resource. Callers must
// use the FinOpsDesk catalog and the same database principal. LockOwner is
// Transaction, so the lock ends only when that transaction commits or rolls back.
internal static class SqlApplicationLock
{
    public static string ResourceName(Guid organizationId)
    {
        return "finopsdesk-auth:" + organizationId.ToString("D", CultureInfo.InvariantCulture);
    }

    public static async Task<int> AcquireAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid organizationId,
        string mode,
        SqlApplicationLockPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentNullException.ThrowIfNull(policy);

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        int timeout = await ResolveTimeoutAsync(connection, transaction, policy, cancellationToken);
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = @mode,
                @LockOwner = 'Transaction',
                @LockTimeout = @timeout,
                @DbPrincipal = N'public';
            SELECT @result;
            """;
        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255)
        {
            Value = ResourceName(organizationId),
        });
        command.Parameters.Add(new SqlParameter("@mode", SqlDbType.NVarChar, 32)
        {
            Value = mode,
        });
        command.Parameters.Add(new SqlParameter("@timeout", SqlDbType.Int)
        {
            Value = timeout,
        });
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        if (!TryReadReturnCode(result, out int code))
        {
            return -999;
        }

        return code;
    }

    // Convert.ToInt32(null) is 0, and 0 means the lock was acquired. A missing
    // or non-integer procedure result must stay outside that success range.
    public static bool TryReadReturnCode(object? result, out int code)
    {
        if (result is int value)
        {
            code = value;
            return true;
        }

        code = 0;
        return false;
    }

    public static bool IsAcquired(int returnCode)
    {
        return returnCode >= 0;
    }

    private static async Task<int> ResolveTimeoutAsync(
        DbConnection connection,
        DbTransaction transaction,
        SqlApplicationLockPolicy policy,
        CancellationToken cancellationToken)
    {
        int policyTimeout = policy.WaitTimeoutMilliseconds;
        if (policyTimeout < 0)
        {
            policyTimeout = SqlApplicationLockPolicy.DefaultWaitTimeoutMilliseconds;
        }

        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT @@LOCK_TIMEOUT;";
        object? raw = await command.ExecuteScalarAsync(cancellationToken);
        int sessionTimeout = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
        if (sessionTimeout < 0)
        {
            return policyTimeout;
        }

        return Math.Min(sessionTimeout, policyTimeout);
    }
}