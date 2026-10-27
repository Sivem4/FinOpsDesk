using System.Data;

using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;

internal sealed class BatchExecutionOwner : IBatchExecutionOwner
{
    private const int CommandTimeoutSeconds = 15;

    private readonly ReconciliationDbContext _dbContext;

    public BatchExecutionOwner(ReconciliationDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public async Task<Result<IBatchExecutionLease>> TryAcquireAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException(
                "Idempotency key cannot be empty.",
                nameof(idempotencyKey));
        }

        string? connectionString = _dbContext.Database.GetConnectionString();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Batch execution ownership requires a SQL Server connection string.");
        }

        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        SqlTransaction? transaction = null;

        try
        {
            transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
            BatchExecutionSnapshot snapshot = await ReadLockedAsync(
                connection,
                transaction,
                organizationId,
                idempotencyKey,
                cancellationToken);
            int sessionId = await ReadSessionIdAsync(connection, transaction, cancellationToken);

            return Result<IBatchExecutionLease>.Success(
                new BatchExecutionLease(connection, transaction, snapshot, sessionId));
        }
        catch (SqlException exception) when (exception.Number == 1222)
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }

            await connection.DisposeAsync();
            return Result<IBatchExecutionLease>.Failure(
                ReconciliationApplicationErrors.BatchExecutionInProgress);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }

            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<BatchExecutionSnapshot> ReadLockedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        OrganizationId organizationId,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT
                [Id],
                [OrganizationId],
                [IdempotencyKey],
                [Fingerprint],
                [Status],
                [ResultVersion],
                [ResultPayload],
                [CreatedAt],
                [CompletedAt]
            FROM [reconciliation].[BatchExecutions] WITH (UPDLOCK, ROWLOCK, NOWAIT)
            WHERE [OrganizationId] = @organizationId
                AND [IdempotencyKey] = @idempotencyKey
            """,
            connection,
            transaction);
        command.CommandTimeout = CommandTimeoutSeconds;
        command.Parameters.Add("@organizationId", SqlDbType.UniqueIdentifier).Value = organizationId.Value;
        command.Parameters.Add("@idempotencyKey", SqlDbType.UniqueIdentifier).Value = idempotencyKey;

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Batch execution was not found for ownership.");
        }

        byte[] fingerprint = (byte[])reader.GetValue(3);
        int? resultVersion = reader.IsDBNull(5) ? null : reader.GetInt32(5);
        string? resultPayload = reader.IsDBNull(6) ? null : reader.GetString(6);
        DateTimeOffset? completedAt = reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8);

        return new BatchExecutionSnapshot(
            new BatchExecutionId(reader.GetGuid(0)),
            new OrganizationId(reader.GetGuid(1)),
            reader.GetGuid(2),
            new BatchExecutionFingerprint(fingerprint),
            BatchExecutionStatusCodec.ToStatus(reader.GetInt32(4)),
            resultVersion,
            resultPayload,
            reader.GetFieldValue<DateTimeOffset>(7),
            completedAt);
    }

    private static async Task<int> ReadSessionIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT @@SPID", connection, transaction);
        command.CommandTimeout = CommandTimeoutSeconds;
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class BatchExecutionLease : IBatchExecutionLease
    {
        private readonly SqlConnection _connection;
        private readonly SqlTransaction _transaction;
        private bool _committed;
        private bool _disposed;

        public BatchExecutionLease(
            SqlConnection connection,
            SqlTransaction transaction,
            BatchExecutionSnapshot snapshot,
            int ownerSessionId)
        {
            _connection = connection;
            _transaction = transaction;
            Snapshot = snapshot;
            OwnerSessionId = ownerSessionId;
        }

        public BatchExecutionSnapshot Snapshot { get; }

        public int OwnerSessionId { get; }

        public async Task<bool> TryCompleteAsync(
            BatchExecutionResult result,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(result);
            BatchExecution.EnsureResultMatches(Snapshot.Fingerprint, result);

            if (completedAt == default || completedAt < Snapshot.CreatedAt)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(completedAt),
                    "Batch execution completion time cannot precede creation time.");
            }

            string payload = BatchExecutionResultDocument.Write(result);
            await using var command = new SqlCommand(
                """
                UPDATE [reconciliation].[BatchExecutions]
                SET [Status] = @completed,
                    [ResultVersion] = @resultVersion,
                    [ResultPayload] = @resultPayload,
                    [CompletedAt] = @completedAt
                WHERE [Id] = @id
                    AND [OrganizationId] = @organizationId
                    AND [Status] = @running
                    AND [ResultVersion] IS NULL
                    AND [ResultPayload] IS NULL
                    AND [CompletedAt] IS NULL
                    AND @completedAt >= [CreatedAt]
                """,
                _connection,
                _transaction);
            command.CommandTimeout = CommandTimeoutSeconds;
            command.Parameters.Add("@completed", SqlDbType.Int).Value =
                BatchExecutionStatusCodec.ToInt(BatchExecutionStatus.Completed);
            command.Parameters.Add("@running", SqlDbType.Int).Value =
                BatchExecutionStatusCodec.ToInt(BatchExecutionStatus.Running);
            command.Parameters.Add("@resultVersion", SqlDbType.Int).Value = result.ResultVersion;
            command.Parameters.Add("@resultPayload", SqlDbType.NVarChar, -1).Value = payload;
            command.Parameters.Add("@completedAt", SqlDbType.DateTimeOffset).Value = completedAt;
            command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = Snapshot.Id.Value;
            command.Parameters.Add("@organizationId", SqlDbType.UniqueIdentifier).Value =
                Snapshot.OrganizationId.Value;

            int updated = await command.ExecuteNonQueryAsync(cancellationToken);
            return updated == 1;
        }

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _transaction.CommitAsync(cancellationToken);
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                if (!_committed)
                {
                    await TryRollbackAsync();
                }
            }
            finally
            {
                await TryDisposeAsync(_transaction);
                await TryDisposeAsync(_connection);
            }
        }

        private async Task TryRollbackAsync()
        {
            try
            {
                await _transaction.RollbackAsync();
            }
            catch (Exception exception) when (IsBrokenSession(exception))
            {
            }
        }

        private static async Task TryDisposeAsync(IAsyncDisposable resource)
        {
            try
            {
                await resource.DisposeAsync();
            }
            catch (Exception exception) when (IsBrokenSession(exception))
            {
            }
        }

        private static bool IsBrokenSession(Exception exception)
        {
            return exception is SqlException
                or InvalidOperationException
                or ObjectDisposedException
                or IOException;
        }
    }
}