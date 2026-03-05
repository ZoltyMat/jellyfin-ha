using System;
using System.Linq;
using System.Threading;
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
/// Integration tests for CRUD operations, optimisation, and purge against a real PostgreSQL 16 container.
/// </summary>
public sealed class PostgreSqlProviderTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container;
    private NpgsqlDataSource? _dataSource;
    private PostgreSqlDatabaseProvider? _provider;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlProviderTests"/> class.
    /// </summary>
    public PostgreSqlProviderTests()
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
    /// Verifies Create/Read/Update/Delete operations on <see cref="User"/>.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task Crud_User()
    {
        var ctx = CreateContext();
        await using (ctx)
        {
            // Create
            var user = new User("testuser", "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider");
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync();

            var userId = user.Id;

            // Read
            var read = await ctx.Users.FindAsync(userId);
            Assert.NotNull(read);
            Assert.Equal("testuser", read.Username);

            // Update
            read.Username = "updateduser";
            await ctx.SaveChangesAsync();

            var updated = await ctx.Users.FindAsync(userId);
            Assert.Equal("updateduser", updated!.Username);

            // Delete
            ctx.Users.Remove(updated);
            await ctx.SaveChangesAsync();

            var deleted = await ctx.Users.FindAsync(userId);
            Assert.Null(deleted);
        }
    }

    /// <summary>
    /// Verifies Create/Read/Update/Delete operations on <see cref="ActivityLog"/>.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task Crud_ActivityLog()
    {
        var ctx = CreateContext();
        await using (ctx)
        {
            // Create
            var log = new ActivityLog("Test activity", "TestType", Guid.Empty);
            ctx.ActivityLogs.Add(log);
            await ctx.SaveChangesAsync();

            var logId = log.Id;

            // Read
            var read = await ctx.ActivityLogs.FindAsync(logId);
            Assert.NotNull(read);
            Assert.Equal("Test activity", read.Name);

            // Update
            read.Overview = "Updated overview";
            await ctx.SaveChangesAsync();

            var updated = await ctx.ActivityLogs.FindAsync(logId);
            Assert.Equal("Updated overview", updated!.Overview);

            // Delete
            ctx.ActivityLogs.Remove(updated);
            await ctx.SaveChangesAsync();

            var deleted = await ctx.ActivityLogs.FindAsync(logId);
            Assert.Null(deleted);
        }
    }

    /// <summary>
    /// Verifies Create/Read/Update/Delete operations on <see cref="DisplayPreferences"/>.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task Crud_DisplayPreferences()
    {
        var ctx = CreateContext();
        await using (ctx)
        {
            var userId = Guid.NewGuid();
            var itemId = Guid.NewGuid();

            // Create
            var prefs = new DisplayPreferences(userId, itemId, "TestClient");
            ctx.DisplayPreferences.Add(prefs);
            await ctx.SaveChangesAsync();

            var prefsId = prefs.Id;

            // Read
            var read = await ctx.DisplayPreferences.FindAsync(prefsId);
            Assert.NotNull(read);
            Assert.Equal("TestClient", read.Client);

            // Update
            read.ShowSidebar = true;
            await ctx.SaveChangesAsync();

            var updated = await ctx.DisplayPreferences.FindAsync(prefsId);
            Assert.True(updated!.ShowSidebar);

            // Delete
            ctx.DisplayPreferences.Remove(updated);
            await ctx.SaveChangesAsync();

            var deleted = await ctx.DisplayPreferences.FindAsync(prefsId);
            Assert.Null(deleted);
        }
    }

    /// <summary>
    /// Verifies Create/Read/Update/Delete operations on <see cref="BaseItemEntity"/>, <see cref="Chapter"/>, and <see cref="MediaStreamInfo"/>.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task Crud_BaseItem_Chapter_MediaStream()
    {
        var ctx = CreateContext();
        await using (ctx)
        {
            var itemId = Guid.NewGuid();

            // Create BaseItem
            var item = new BaseItemEntity { Id = itemId, Type = "Movie", Name = "Test Movie" };
            ctx.BaseItems.Add(item);
            await ctx.SaveChangesAsync();

            // Create Chapter linked to BaseItem
            var chapter = new Chapter { ItemId = itemId, Item = item, ChapterIndex = 0, StartPositionTicks = 0, Name = "Intro" };
            ctx.Chapters.Add(chapter);

            // Create MediaStreamInfo linked to BaseItem
            var stream = new MediaStreamInfo { ItemId = itemId, Item = item, StreamIndex = 0, StreamType = MediaStreamTypeEntity.Video };
            ctx.MediaStreamInfos.Add(stream);

            await ctx.SaveChangesAsync();

            // Read
            var readItem = await ctx.BaseItems
                .Include(i => i.Chapters)
                .Include(i => i.MediaStreams)
                .FirstOrDefaultAsync(i => i.Id.Equals(itemId));

            Assert.NotNull(readItem);
            Assert.Equal("Test Movie", readItem.Name);
            Assert.Single(readItem.Chapters!);
            Assert.Single(readItem.MediaStreams!);

            // Update
            readItem.Name = "Updated Movie";
            await ctx.SaveChangesAsync();

            var updated = await ctx.BaseItems.FindAsync(itemId);
            Assert.Equal("Updated Movie", updated!.Name);

            // Delete (cascades to Chapter and MediaStreamInfo)
            ctx.BaseItems.Remove(updated);
            await ctx.SaveChangesAsync();

            var deleted = await ctx.BaseItems.FindAsync(itemId);
            Assert.Null(deleted);
        }
    }

    /// <summary>
    /// Verifies that <see cref="PostgreSqlDatabaseProvider.RunScheduledOptimisation"/> executes ANALYZE without error.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task RunScheduledOptimisation_ExecutesWithoutError()
    {
        var ctx = CreateContext();
        await using (ctx)
        {
            var factory = new TestDbContextFactory(ctx);
            _provider!.DbContextFactory = factory;

            await _provider.RunScheduledOptimisation(CancellationToken.None);
        }
    }

    /// <summary>
    /// Verifies that <see cref="PostgreSqlDatabaseProvider.PurgeDatabase"/> empties tables and resets <c>session_replication_role</c>.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task PurgeDatabase_EmptiesTablesAndResetsFkRole()
    {
        var ctx = CreateContext();
        await using (ctx)
        {
            // Seed a row
            ctx.ActivityLogs.Add(new ActivityLog("Purge test", "TestType", Guid.Empty));
            await ctx.SaveChangesAsync();

            Assert.True(await ctx.ActivityLogs.AnyAsync());

            // Purge
            await _provider!.PurgeDatabase(ctx, ["ActivityLogs"]);

            // session_replication_role should be reset to 'origin' (default)
            var role = await ctx.Database
                .SqlQueryRaw<string>("SELECT current_setting('session_replication_role')")
                .FirstAsync();
            Assert.Equal("origin", role);
        }

        // Verify table is empty via a fresh context
        var freshCtx = CreateContext();
        await using (freshCtx)
        {
            Assert.False(await freshCtx.ActivityLogs.AnyAsync());
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

    /// <summary>
    /// A minimal <see cref="IDbContextFactory{TContext}"/> wrapper that returns a pre-existing context.
    /// </summary>
    private sealed class TestDbContextFactory : IDbContextFactory<JellyfinDbContext>
    {
        private readonly JellyfinDbContext _context;

        /// <summary>
        /// Initializes a new instance of the <see cref="TestDbContextFactory"/> class.
        /// </summary>
        /// <param name="context">The context to return from <see cref="CreateDbContext"/>.</param>
        public TestDbContextFactory(JellyfinDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Returns the pre-existing <see cref="JellyfinDbContext"/> instance.
        /// </summary>
        /// <returns>The pre-existing <see cref="JellyfinDbContext"/> instance.</returns>
        public JellyfinDbContext CreateDbContext() => _context;

        /// <summary>
        /// Returns the pre-existing <see cref="JellyfinDbContext"/> instance as a completed task.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token (unused).</param>
        /// <returns>A <see cref="Task{TResult}"/> containing the pre-existing <see cref="JellyfinDbContext"/> instance.</returns>
        public Task<JellyfinDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_context);
    }
}
