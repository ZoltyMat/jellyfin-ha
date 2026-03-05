using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Database.Providers.PostgreSQL;

/// <summary>
/// Configures Jellyfin to use a PostgreSQL database.
/// </summary>
[JellyfinDatabaseProviderKey("Jellyfin-PostgreSQL")]
public sealed class PostgreSqlDatabaseProvider : IJellyfinDatabaseProvider
{
    private const string BackupNotSupportedMessage =
        "Automated migration backups are not supported for PostgreSQL. Use the jellyfin-pg-backup CronJob for nightly S3 backups.";

    /// <inheritdoc/>
    public IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

    /// <inheritdoc/>
    public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
            ?? databaseConfiguration.CustomProviderOptions?.Options
                ?.FirstOrDefault(o => o.Key.Equals("ConnectionString", StringComparison.OrdinalIgnoreCase))
                ?.Value
            ?? databaseConfiguration.CustomProviderOptions?.ConnectionString
            ?? throw new InvalidOperationException(
                "No PostgreSQL connection string found. Set the POSTGRES_CONNECTION_STRING environment variable, " +
                "or provide it via CustomProviderOptions.Options[\"ConnectionString\"] or CustomProviderOptions.ConnectionString.");

        options.UseNpgsql(
            connectionString,
            o => o.MigrationsAssembly(GetType().Assembly.FullName));
    }

    /// <inheritdoc/>
    public void OnModelCreating(ModelBuilder modelBuilder)
    {
    }

    /// <inheritdoc/>
    public void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
    }

    /// <inheritdoc/>
    public async Task RunScheduledOptimisation(CancellationToken cancellationToken)
    {
        var context = await DbContextFactory!.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            await context.Database.ExecuteSqlRawAsync("ANALYZE", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public Task RunShutdownTask(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<string> MigrationBackupFast(CancellationToken cancellationToken)
    {
        throw new NotSupportedException(BackupNotSupportedMessage);
    }

    /// <inheritdoc/>
    public Task RestoreBackupFast(string key, CancellationToken cancellationToken)
    {
        throw new NotSupportedException(BackupNotSupportedMessage);
    }

    /// <inheritdoc/>
    public Task DeleteBackup(string key)
    {
        throw new NotSupportedException(BackupNotSupportedMessage);
    }

    /// <inheritdoc/>
    public async Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames)
    {
        ArgumentNullException.ThrowIfNull(tableNames);

        await dbContext.Database.ExecuteSqlRawAsync("SET session_replication_role = 'replica'").ConfigureAwait(false);
        try
        {
            foreach (var tableName in tableNames)
            {
                var truncateSql = "TRUNCATE TABLE \"" + tableName + "\" CASCADE";
                await dbContext.Database.ExecuteSqlRawAsync(truncateSql).ConfigureAwait(false);
            }
        }
        finally
        {
            await dbContext.Database.ExecuteSqlRawAsync("SET session_replication_role = 'origin'").ConfigureAwait(false);
        }
    }
}
