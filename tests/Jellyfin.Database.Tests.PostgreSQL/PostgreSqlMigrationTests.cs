using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Jellyfin.Database.Tests.PostgreSQL;

/// <summary>
/// Integration tests that validate PostgreSQL migrations against a real container.
/// </summary>
[Xunit.Trait("Category", "RequiresDocker")]
public sealed class PostgreSqlMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlMigrationTests"/> class.
    /// </summary>
    public PostgreSqlMigrationTests()
    {
        _container = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready"))
            .Build();
    }

    /// <summary>
    /// Starts the PostgreSQL container before any tests in the class run.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task InitializeAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Stops and removes the PostgreSQL container after all tests in the class have run.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task DisposeAsync()
    {
        await _container.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that the <c>InitialPostgreSql</c> migration applies cleanly to a fresh PostgreSQL 16 container.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task MigrateAsync_AppliesInitialMigrationCleanly()
    {
        await using var dataSource = new NpgsqlDataSourceBuilder(_container.GetConnectionString()).Build();
        var context = CreateContext(dataSource);
        await using (context)
        {
            await context.Database.MigrateAsync();

            var pendingMigrations = await context.Database.GetPendingMigrationsAsync();
            Assert.Empty(pendingMigrations);
        }
    }

    /// <summary>
    /// Verifies that no pending model changes exist for the PostgreSQL provider,
    /// acting as a CI gate that fails when model changes are added without a corresponding migration.
    /// </summary>
    [Fact]
    public void CheckForUnappliedMigrations_PostgreSql()
    {
        // Use a dummy connection string; HasPendingModelChanges() is a purely in-memory check
        // that compares the current compiled model with the migration snapshots — no real DB needed.
        const string dummyConnectionString = "Host=localhost;Database=jellyfin;Username=postgres;Password=postgres";
        using var dataSource = new NpgsqlDataSourceBuilder(dummyConnectionString).Build();
        using var context = CreateContext(dataSource);

        Assert.False(
            context.Database.HasPendingModelChanges(),
            "There are unapplied changes to the EFCore model for PostgreSQL. Please create a Migration.");
    }

    private static JellyfinDbContext CreateContext(NpgsqlDataSource dataSource)
    {
        var optionsBuilder = new DbContextOptionsBuilder<JellyfinDbContext>();
        var provider = new PostgreSqlDatabaseProvider(dataSource);
        provider.Initialise(optionsBuilder, new DatabaseConfigurationOptions { DatabaseType = "PostgreSQL" });
        return new JellyfinDbContext(
            optionsBuilder.Options,
            NullLogger<JellyfinDbContext>.Instance,
            provider,
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }
}
