using System.Data;
using System.Data.Common;

using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

internal sealed record SqlIndex(
    bool IsUnique,
    bool HasFilter,
    string? FilterDefinition,
    IReadOnlyList<string> Columns);

internal static class SqlServerCatalog
{
    public static SqlException RequireSqlException(Exception exception)
    {
        Exception? current = exception;

        while (current is not null)
        {
            if (current is SqlException sqlException)
            {
                return sqlException;
            }

            current = current.InnerException;
        }

        throw new InvalidOperationException(
            "The database operation did not raise a SqlException.",
            exception);
    }

    public static async Task<SqlIndex?> FindIndexAsync(
        ReconciliationDbContext context,
        string tableName,
        string indexName)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);

        (bool IsUnique, bool HasFilter, string? Filter)? header = await ExecuteAsync(
            context,
            async command =>
            {
                command.CommandText =
                    """
                    SELECT i.is_unique, i.has_filter, i.filter_definition
                    FROM sys.indexes AS i
                    INNER JOIN sys.tables AS t
                        ON i.object_id = t.object_id
                    INNER JOIN sys.schemas AS s
                        ON t.schema_id = s.schema_id
                    WHERE s.name = N'reconciliation'
                        AND t.name = @tableName
                        AND i.name = @indexName;
                    """;
                AddParameter(command, "@tableName", tableName);
                AddParameter(command, "@indexName", indexName);

                await using DbDataReader reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    return ((bool, bool, string?)?)null;
                }

                string? filter = reader.IsDBNull(2) ? null : reader.GetString(2);
                return (reader.GetBoolean(0), reader.GetBoolean(1), filter);
            });

        if (header is null)
        {
            return null;
        }

        IReadOnlyList<string> columns = await ExecuteAsync(
            context,
            async command =>
            {
                command.CommandText =
                    """
                    SELECT c.name
                    FROM sys.indexes AS i
                    INNER JOIN sys.tables AS t
                        ON i.object_id = t.object_id
                    INNER JOIN sys.schemas AS s
                        ON t.schema_id = s.schema_id
                    INNER JOIN sys.index_columns AS ic
                        ON i.object_id = ic.object_id
                        AND i.index_id = ic.index_id
                    INNER JOIN sys.columns AS c
                        ON ic.object_id = c.object_id
                        AND ic.column_id = c.column_id
                    WHERE s.name = N'reconciliation'
                        AND t.name = @tableName
                        AND i.name = @indexName
                        AND ic.is_included_column = 0
                    ORDER BY ic.key_ordinal;
                    """;
                AddParameter(command, "@tableName", tableName);
                AddParameter(command, "@indexName", indexName);

                var names = new List<string>();
                await using DbDataReader reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    names.Add(reader.GetString(0));
                }

                return (IReadOnlyList<string>)names;
            });

        return new SqlIndex(
            header.Value.IsUnique,
            header.Value.HasFilter,
            header.Value.Filter,
            columns);
    }

    public static async Task<string?> FindCheckDefinitionAsync(
        ReconciliationDbContext context,
        string tableName,
        string constraintName)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintName);

        return await ExecuteAsync(
            context,
            async command =>
            {
                command.CommandText =
                    """
                    SELECT cc.definition
                    FROM sys.check_constraints AS cc
                    INNER JOIN sys.tables AS t
                        ON cc.parent_object_id = t.object_id
                    INNER JOIN sys.schemas AS s
                        ON t.schema_id = s.schema_id
                    WHERE s.name = N'reconciliation'
                        AND t.name = @tableName
                        AND cc.name = @constraintName;
                    """;
                AddParameter(command, "@tableName", tableName);
                AddParameter(command, "@constraintName", constraintName);

                object? result = await command.ExecuteScalarAsync();
                return result is null or DBNull ? null : (string)result;
            });
    }

    public static async Task<string?> FindColumnCollationAsync(
        ReconciliationDbContext context,
        string tableName,
        string columnName)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);

        return await ExecuteAsync(
            context,
            async command =>
            {
                command.CommandText =
                    """
                    SELECT c.collation_name
                    FROM sys.columns AS c
                    INNER JOIN sys.tables AS t
                        ON c.object_id = t.object_id
                    INNER JOIN sys.schemas AS s
                        ON t.schema_id = s.schema_id
                    WHERE s.name = N'reconciliation'
                        AND t.name = @tableName
                        AND c.name = @columnName;
                    """;
                AddParameter(command, "@tableName", tableName);
                AddParameter(command, "@columnName", columnName);

                object? result = await command.ExecuteScalarAsync();
                return result is null or DBNull ? null : (string)result;
            });
    }

    public static async Task<string?> ScalarStringAsync(
        ReconciliationDbContext context,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        return await ExecuteAsync(
            context,
            async command =>
            {
                command.CommandText = sql;
                foreach ((string name, object? value) in parameters)
                {
                    AddParameter(command, name, value);
                }

                object? result = await command.ExecuteScalarAsync();
                return result is null or DBNull ? null : Convert.ToString(result);
            });
    }

    private static async Task<T> ExecuteAsync<T>(
        ReconciliationDbContext context,
        Func<DbCommand, Task<T>> execute)
    {
        DbConnection connection = context.Database.GetDbConnection();
        bool shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
        {
            await connection.OpenAsync();
        }

        try
        {
            await using DbCommand command = connection.CreateCommand();
            return await execute(command);
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}