using System;
using System.Collections.Generic;

namespace Jellyfin.DbMigrator;

/// <summary>
/// Represents the migration result for a single table.
/// </summary>
/// <param name="TableName">The name of the table.</param>
/// <param name="SqliteRowCount">The number of rows read from SQLite.</param>
/// <param name="PostgresRowCount">The number of rows verified in PostgreSQL after migration.</param>
/// <param name="Error">The error message if migration failed, or <see langword="null"/> on success.</param>
public sealed record TableReport(
    string TableName,
    long SqliteRowCount,
    long PostgresRowCount,
    string? Error);

/// <summary>
/// Provides utilities for collecting and printing the migration report.
/// </summary>
public static class MigrationReport
{
    /// <summary>
    /// Prints a formatted summary of per-table migration results to the console.
    /// </summary>
    /// <param name="reports">The collection of per-table results.</param>
    public static void Print(IReadOnlyList<TableReport> reports)
    {
        Console.WriteLine();
        Console.WriteLine("=== Migration Report ===");
        Console.WriteLine(
            $"{"Table",-40} {"SQLite",10} {"PostgreSQL",10} {"Status",-10}");
        Console.WriteLine(new string('-', 74));

        int failed = 0;
        foreach (var r in reports)
        {
            string status = r.Error is null ? "OK" : "FAILED";
            if (r.Error is not null)
            {
                failed++;
            }

            Console.WriteLine(
                $"{r.TableName,-40} {r.SqliteRowCount,10} {r.PostgresRowCount,10} {status,-10}");

            if (r.Error is not null)
            {
                Console.WriteLine($"  Error: {r.Error}");
            }
        }

        Console.WriteLine(new string('-', 74));
        Console.WriteLine(
            $"Total: {reports.Count} tables, {failed} failed, {reports.Count - failed} succeeded.");
    }
}
