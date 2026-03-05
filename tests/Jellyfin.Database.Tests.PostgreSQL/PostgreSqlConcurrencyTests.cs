using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Jellyfin.Database.Tests.PostgreSQL;

/// <summary>
/// Integration tests that verify concurrent access patterns against a real PostgreSQL 16 container.
/// </summary>
[Xunit.Trait("Category", "RequiresDocker")]
public sealed class PostgreSqlConcurrencyTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container;
    private NpgsqlDataSource? _dataSource;
    private PostgreSqlDatabaseProvider? _provider;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlConcurrencyTests"/> class.
    /// </summary>
    public PostgreSqlConcurrencyTests()
    {
        _container = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready"))
            .Build();
    }

    /// <summary>
    /// Starts the PostgreSQL container and applies migrations before any tests in the class run.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task InitializeAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);

        _dataSource = new NpgsqlDataSourceBuilder(_container.GetConnectionString()).Build();
        _provider = new PostgreSqlDatabaseProvider(_dataSource);

        // Apply migrations once for the whole test class.
        var context = CreateContext();
        await using (context.ConfigureAwait(false))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops and removes the PostgreSQL container after all tests in the class have run.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }

        await _container.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that concurrent inserts on <see cref="ActivityLog"/> from four parallel tasks succeed without deadlock.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task ConcurrentInserts_ActivityLogs_SucceedWithoutDeadlock()
    {
        const int parallelTasks = 4;
        const int insertsPerTask = 10;

        var tasks = new List<Task>(parallelTasks);
        for (var i = 0; i < parallelTasks; i++)
        {
            var taskIndex = i;
            tasks.Add(Task.Run(async () =>
            {
                var ctx = CreateContext();
                await using (ctx.ConfigureAwait(false))
                {
                    for (var j = 0; j < insertsPerTask; j++)
                    {
                        ctx.ActivityLogs.Add(new ActivityLog(
                            $"Task {taskIndex} Insert {j}",
                            "ConcurrencyTest",
                            Guid.Empty));
                    }

                    await ctx.SaveChangesAsync().ConfigureAwait(false);
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Verify all rows were inserted
        var verifyCtx = CreateContext();
        await using (verifyCtx)
        {
            var count = await verifyCtx.ActivityLogs
                .CountAsync(l => l.Type == "ConcurrencyTest");
            Assert.Equal(parallelTasks * insertsPerTask, count);
        }
    }

    private JellyfinDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<JellyfinDbContext>();
        _provider!.Initialise(optionsBuilder, new DatabaseConfigurationOptions { DatabaseType = "PostgreSQL" });
        return new JellyfinDbContext(
            optionsBuilder.Options,
            NullLogger<JellyfinDbContext>.Instance,
            _provider,
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }
}
