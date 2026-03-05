using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Jellyfin.DbMigrator;

/// <summary>
/// Writes rows to a PostgreSQL database using batched INSERT statements.
/// </summary>
public static class PostgresBulkWriter
{
    /// <summary>
    /// The maximum number of rows per INSERT batch.
    /// </summary>
    private const int BatchSize = 500;

    /// <summary>
    /// Inserts all rows into the specified PostgreSQL table using batched INSERT statements.
    /// When <paramref name="isDryRun"/> is <see langword="true"/>, logs what would be inserted without writing.
    /// </summary>
    /// <param name="connection">An open <see cref="NpgsqlConnection"/>.</param>
    /// <param name="tableName">The name of the target PostgreSQL table.</param>
    /// <param name="rows">The rows to insert, as dictionaries mapping column name to value.</param>
    /// <param name="isDryRun">When <see langword="true"/>, skips actual writes.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The number of rows that were inserted (or would have been inserted in dry-run mode).</returns>
    public static async Task<long> WriteTableAsync(
        NpgsqlConnection connection,
        string tableName,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        bool isDryRun,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        TableNameValidator.EnsureSafe(tableName);
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0)
        {
            return 0L;
        }

        // Collect column names from the first row.
        var columns = new List<string>(rows[0].Keys);

        if (isDryRun)
        {
            Console.WriteLine(
                $"  [dry-run] Would insert {rows.Count} rows into \"{tableName}\" " +
                $"({string.Join(", ", columns)}).");
            return rows.Count;
        }

        long inserted = 0L;

        for (int offset = 0; offset < rows.Count; offset += BatchSize)
        {
            int end = Math.Min(offset + BatchSize, rows.Count);
            int batchCount = end - offset;

            var sql = BuildInsertSql(tableName, columns, batchCount);

            var cmd = connection.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = sql;

                int paramIndex = 0;
                for (int rowIdx = offset; rowIdx < end; rowIdx++)
                {
                    var row = rows[rowIdx];
                    foreach (var col in columns)
                    {
                        string paramName = $"p{paramIndex.ToString(CultureInfo.InvariantCulture)}";
                        row.TryGetValue(col, out object? val);
                        cmd.Parameters.AddWithValue(paramName, val ?? DBNull.Value);
                        paramIndex++;
                    }
                }

                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                inserted += batchCount;
            }
        }

        return inserted;
    }

    /// <summary>
    /// Advances the PostgreSQL integer sequence for each table that contains an <c>Id</c> column,
    /// so that future auto-generated primary keys do not conflict with migrated data.
    /// </summary>
    /// <param name="connection">An open <see cref="NpgsqlConnection"/>.</param>
    /// <param name="tableNames">The names of the tables whose sequences should be advanced.</param>
    /// <param name="isDryRun">When <see langword="true"/>, logs the SQL without executing it.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public static async Task AdvanceSequencesAsync(
        NpgsqlConnection connection,
        IEnumerable<string> tableNames,
        bool isDryRun,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tableNames);

        foreach (var tableName in tableNames)
        {
            // Check if the table has an "Id" column.
            bool hasIdColumn = await TableHasColumnAsync(
                connection, tableName, "Id", cancellationToken).ConfigureAwait(false);

            if (!hasIdColumn)
            {
                continue;
            }

            string sql =
                $"SELECT setval(pg_get_serial_sequence('{tableName}', 'Id'), " +
                $"COALESCE((SELECT MAX(\"Id\") FROM \"{tableName}\"), 1))";

            if (isDryRun)
            {
                Console.WriteLine($"  [dry-run] Would advance sequence: {sql}");
                continue;
            }

            var cmd = connection.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = sql;
                try
                {
                    await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Sequence may not exist for tables without serial PK — log and continue.
                    Console.WriteLine(
                        $"  Warning: Could not advance sequence for \"{tableName}\": {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Returns the number of rows currently in the specified PostgreSQL table.
    /// </summary>
    /// <param name="connection">An open <see cref="NpgsqlConnection"/>.</param>
    /// <param name="tableName">The name of the table to count.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The row count, or -1 if the table does not exist.</returns>
    public static async Task<long> CountRowsAsync(
        NpgsqlConnection connection,
        string tableName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        TableNameValidator.EnsureSafe(tableName);

        var cmd = connection.CreateCommand();
        await using (cmd.ConfigureAwait(false))
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM \"{tableName}\"";
            try
            {
                var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return result is long count ? count : Convert.ToInt64(result, CultureInfo.InvariantCulture);
            }
            catch (NpgsqlException)
            {
                return -1L;
            }
        }
    }

    /// <summary>
    /// Builds a parameterised bulk INSERT SQL statement for the given table, columns, and row count.
    /// </summary>
    /// <param name="tableName">The target table name.</param>
    /// <param name="columns">The ordered list of column names.</param>
    /// <param name="rowCount">The number of value-rows to include.</param>
    /// <returns>A parameterised INSERT statement.</returns>
    private static string BuildInsertSql(string tableName, IReadOnlyList<string> columns, int rowCount)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"INSERT INTO \"{tableName}\" (");

        for (int i = 0; i < columns.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(CultureInfo.InvariantCulture, $"\"{columns[i]}\"");
        }

        sb.Append(") VALUES ");

        int paramIndex = 0;
        for (int row = 0; row < rowCount; row++)
        {
            if (row > 0)
            {
                sb.Append(", ");
            }

            sb.Append('(');
            for (int col = 0; col < columns.Count; col++)
            {
                if (col > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(CultureInfo.InvariantCulture, $"@p{paramIndex.ToString(CultureInfo.InvariantCulture)}");
                paramIndex++;
            }

            sb.Append(')');
        }

        sb.Append(" ON CONFLICT DO NOTHING");

        return sb.ToString();
    }

    /// <summary>
    /// Checks whether a given column exists in a PostgreSQL table.
    /// </summary>
    /// <param name="connection">An open <see cref="NpgsqlConnection"/>.</param>
    /// <param name="tableName">The table name to check.</param>
    /// <param name="columnName">The column name to look for.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> if the column exists; otherwise, <see langword="false"/>.</returns>
    private static async Task<bool> TableHasColumnAsync(
        NpgsqlConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken = default)
    {
        var cmd = connection.CreateCommand();
        await using (cmd.ConfigureAwait(false))
        {
            cmd.CommandText =
                "SELECT COUNT(*) FROM information_schema.columns " +
                "WHERE table_name = @table AND column_name = @col";
            cmd.Parameters.AddWithValue("table", tableName);
            cmd.Parameters.AddWithValue("col", columnName);
            var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            long count = result is long l ? l : Convert.ToInt64(result, CultureInfo.InvariantCulture);
            return count > 0;
        }
    }
}
