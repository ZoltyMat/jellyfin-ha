using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.S3;
using Amazon.S3.Transfer;
using Jellyfin.DbMigrator;
using Microsoft.Data.Sqlite;
using Npgsql;

// ---------------------------------------------------------------------------
// Ordered table list (respects FK constraints).
// ---------------------------------------------------------------------------
string[] tableOrder =
[
    // Group 1 – no FK dependencies
    "Users",
    "ApiKeys",
    "Devices",
    "DeviceOptions",

    // Group 2 – BaseItems (self-referencing FK only)
    "BaseItems",

    // Group 3 – children of BaseItems + ItemValues
    "AncestorIds",
    "BaseItemImageInfos",
    "BaseItemMetadataFields",
    "BaseItemTrailerTypes",
    "BaseItemProviders",
    "Chapters",
    "ItemValues",
    "ItemValuesMap",
    "MediaStreamInfos",
    "AttachmentStreamInfos",
    "KeyframeData",

    // Group 4 – People
    "Peoples",
    "PeopleBaseItemMap",

    // Group 5 – User-related data
    "UserData",
    "MediaSegments",
    "TrickplayInfos",

    // Group 6 – Misc / user preferences
    "ActivityLogs",
    "AccessSchedules",
    "Permissions",
    "Preferences",
    "DisplayPreferences",
    "ItemDisplayPreferences",
    "CustomItemDisplayPreferences",
    "ImageInfos",
];

// ---------------------------------------------------------------------------
// Parse command-line arguments.
// ---------------------------------------------------------------------------
string? sqlitePath = null;
string? postgresConnectionString = null;
bool isDryRun = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--sqlite" when i + 1 < args.Length:
            sqlitePath = args[++i];
            break;
        case "--postgres" when i + 1 < args.Length:
            postgresConnectionString = args[++i];
            break;
        case "--dry-run":
            isDryRun = true;
            break;
    }
}

if (string.IsNullOrWhiteSpace(sqlitePath) || string.IsNullOrWhiteSpace(postgresConnectionString))
{
    await Console.Error.WriteLineAsync(
        "Usage: Jellyfin.DbMigrator --sqlite <path> --postgres <connection-string> [--dry-run]")
        .ConfigureAwait(false);
    return 2;
}

if (!File.Exists(sqlitePath))
{
    await Console.Error.WriteLineAsync($"SQLite database not found: {sqlitePath}")
        .ConfigureAwait(false);
    return 2;
}

if (isDryRun)
{
    await Console.Out.WriteLineAsync("[dry-run] No data will be written to PostgreSQL.")
        .ConfigureAwait(false);
}

// ---------------------------------------------------------------------------
// Pre-migration S3 backup.
// ---------------------------------------------------------------------------
string? s3Bucket = Environment.GetEnvironmentVariable("S3_BACKUP_BUCKET");
string? awsRegion = Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION");

if (!string.IsNullOrWhiteSpace(s3Bucket) && !string.IsNullOrWhiteSpace(awsRegion))
{
    await Console.Out.WriteLineAsync($"Uploading {sqlitePath} to s3://{s3Bucket}/ in region {awsRegion}…")
        .ConfigureAwait(false);
    try
    {
        await UploadToS3Async(sqlitePath, s3Bucket, awsRegion, isDryRun).ConfigureAwait(false);
        await Console.Out.WriteLineAsync("S3 backup complete.").ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync($"S3 backup failed (continuing): {ex.Message}")
            .ConfigureAwait(false);
    }
}
else
{
    await Console.Out.WriteLineAsync(
        "S3_BACKUP_BUCKET or AWS_DEFAULT_REGION not set – skipping pre-migration backup.")
        .ConfigureAwait(false);
}

// ---------------------------------------------------------------------------
// Open connections.
// ---------------------------------------------------------------------------
var sqliteConnectionString = new SqliteConnectionStringBuilder
{
    DataSource = sqlitePath,
    Mode = SqliteOpenMode.ReadOnly,
}.ToString();

await using var sqliteConnection = new SqliteConnection(sqliteConnectionString);
await sqliteConnection.OpenAsync(CancellationToken.None).ConfigureAwait(false);

await using var pgConnection = new NpgsqlConnection(postgresConnectionString);
await pgConnection.OpenAsync(CancellationToken.None).ConfigureAwait(false);

// ---------------------------------------------------------------------------
// Migrate tables.
// ---------------------------------------------------------------------------
var reports = new List<TableReport>();
bool anyFailure = false;

foreach (var tableName in tableOrder)
{
    await Console.Out.WriteLineAsync($"Migrating table: {tableName}").ConfigureAwait(false);

    long sqliteCount = 0L;
    long pgCount = 0L;
    string? error = null;

    try
    {
        // Read from SQLite.
        sqliteCount = await SqliteTableReader.CountRowsAsync(
            sqliteConnection, tableName).ConfigureAwait(false);

        if (sqliteCount < 0)
        {
            await Console.Out.WriteLineAsync($"  Table \"{tableName}\" not found in SQLite – skipping.")
                .ConfigureAwait(false);
            reports.Add(new TableReport(tableName, 0L, 0L, null));
            continue;
        }

        await Console.Out.WriteLineAsync($"  SQLite rows: {sqliteCount}").ConfigureAwait(false);

        var rows = await SqliteTableReader.ReadAllRowsAsync(
            sqliteConnection, tableName).ConfigureAwait(false);

        // Write to PostgreSQL.
        long inserted = await PostgresBulkWriter.WriteTableAsync(
            pgConnection, tableName, rows, isDryRun).ConfigureAwait(false);

        await Console.Out.WriteLineAsync($"  Inserted: {inserted}").ConfigureAwait(false);

        // Verify row count in PostgreSQL.
        pgCount = isDryRun
            ? 0L
            : await PostgresBulkWriter.CountRowsAsync(pgConnection, tableName).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        error = ex.Message;
        anyFailure = true;
        await Console.Error.WriteLineAsync($"  ERROR migrating \"{tableName}\": {ex.Message}")
            .ConfigureAwait(false);
    }

    reports.Add(new TableReport(tableName, sqliteCount, pgCount, error));
}

// ---------------------------------------------------------------------------
// Advance PostgreSQL sequences.
// ---------------------------------------------------------------------------
await Console.Out.WriteLineAsync("Advancing PostgreSQL sequences…").ConfigureAwait(false);
await PostgresBulkWriter.AdvanceSequencesAsync(
    pgConnection, tableOrder, isDryRun).ConfigureAwait(false);

// ---------------------------------------------------------------------------
// Print report.
// ---------------------------------------------------------------------------
MigrationReport.Print(reports);

return anyFailure ? 1 : 0;

// ---------------------------------------------------------------------------
// Local functions.
// ---------------------------------------------------------------------------

// Uploads a file to the configured S3 bucket before migration starts.
static async Task UploadToS3Async(
    string filePath,
    string bucket,
    string region,
    bool isDryRun)
{
    if (isDryRun)
    {
        await Console.Out.WriteLineAsync(
            $"  [dry-run] Would upload \"{filePath}\" to s3://{bucket}/{Path.GetFileName(filePath)}")
            .ConfigureAwait(false);
        return;
    }

    var regionEndpoint = RegionEndpoint.GetBySystemName(region);
    using var s3Client = new AmazonS3Client(regionEndpoint);
    using var transferUtility = new TransferUtility(s3Client);

    string key = $"jellyfin-db-backups/{Path.GetFileName(filePath)}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.bak";

    await transferUtility.UploadAsync(filePath, bucket, key).ConfigureAwait(false);
    await Console.Out.WriteLineAsync($"  Uploaded to s3://{bucket}/{key}").ConfigureAwait(false);
}

