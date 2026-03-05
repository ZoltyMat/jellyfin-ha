using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Jellyfin.DbMigrator;

/// <summary>
/// Reads rows from a SQLite database table using raw ADO.NET.
/// </summary>
public static class SqliteTableReader
{
    /// <summary>
    /// Returns all rows from the specified SQLite table as a list of column-name-to-value dictionaries.
    /// </summary>
    /// <param name="connection">An open <see cref="SqliteConnection"/>.</param>
    /// <param name="tableName">The name of the table to read.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A list where each element is a dictionary mapping column name to its value (may be <see langword="null"/>).</returns>
    public static async Task<List<IReadOnlyDictionary<string, object?>>> ReadAllRowsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        TableNameValidator.EnsureSafe(tableName);

        var rows = new List<IReadOnlyDictionary<string, object?>>();

        var cmd = connection.CreateCommand();
        await using (cmd.ConfigureAwait(false))
        {
            cmd.CommandText = $"SELECT * FROM \"{tableName}\"";

            var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        string col = reader.GetName(i);
                        bool isNull = await reader.IsDBNullAsync(i, cancellationToken).ConfigureAwait(false);
                        object? val = isNull ? null : reader.GetValue(i);
                        row[col] = val;
                    }

                    rows.Add(row);
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// Returns the row count for the specified table in the SQLite database.
    /// </summary>
    /// <param name="connection">An open <see cref="SqliteConnection"/>.</param>
    /// <param name="tableName">The name of the table to count.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The number of rows in the table, or -1 if the table does not exist.</returns>
    public static async Task<long> CountRowsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        TableNameValidator.EnsureSafe(tableName);

        // Check if the table exists first.
        var checkCmd = connection.CreateCommand();
        await using (checkCmd.ConfigureAwait(false))
        {
            checkCmd.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
            checkCmd.Parameters.AddWithValue("$name", tableName);
            var exists = await checkCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (exists is not long existsLong || existsLong == 0)
            {
                return -1L;
            }
        }

        var cmd = connection.CreateCommand();
        await using (cmd.ConfigureAwait(false))
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM \"{tableName}\"";
            var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is long count ? count : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
