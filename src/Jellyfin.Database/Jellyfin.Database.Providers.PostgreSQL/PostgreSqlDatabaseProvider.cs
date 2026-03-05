using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jellyfin.Database.Providers.PostgreSQL;

/// <summary>
/// Configures Jellyfin to use a PostgreSQL database.
/// </summary>
[JellyfinDatabaseProviderKey("Jellyfin-PostgreSQL")]
public sealed class PostgreSqlDatabaseProvider : IJellyfinDatabaseProvider
{
    // Sentinel returned by MigrationBackupFast to signal that no file backup was
    // created (PostgreSQL backups are handled externally by jellyfin-pg-backup CronJob).
    private const string NoAutomatedBackupKey = "postgresql-no-automated-backup";

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlDatabaseProvider"/> class.
    /// </summary>
    /// <param name="dataSource">The <see cref="NpgsqlDataSource"/> used for PostgreSQL connections.</param>
    public PostgreSqlDatabaseProvider(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    /// <inheritdoc/>
    public IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

    /// <inheritdoc/>
    public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
    {
        options.UseNpgsql(
            _dataSource,
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
        // PostgreSQL pre-migration backups are handled externally by the
        // jellyfin-pg-backup CronJob. Return a sentinel so callers know no
        // file backup was created and the migration can proceed safely.
        return Task.FromResult(NoAutomatedBackupKey);
    }

    /// <inheritdoc/>
    public Task RestoreBackupFast(string key, CancellationToken cancellationToken)
    {
        // No automated backup was taken; nothing to restore.
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DeleteBackup(string key)
    {
        // No automated backup was taken; nothing to delete.
        return Task.CompletedTask;
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
