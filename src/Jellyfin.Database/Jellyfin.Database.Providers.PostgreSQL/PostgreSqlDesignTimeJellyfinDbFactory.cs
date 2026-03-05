using System;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Jellyfin.Database.Providers.PostgreSQL;

/// <summary>
/// The design time factory for <see cref="JellyfinDbContext"/> using PostgreSQL.
/// This is only used for the creation of migrations and not during runtime.
/// </summary>
internal sealed class PostgreSqlDesignTimeJellyfinDbFactory : IDesignTimeDbContextFactory<JellyfinDbContext>
{
    /// <inheritdoc/>
    public JellyfinDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
            ?? "Host=localhost;Database=jellyfin;Username=postgres;Password=postgres";

        var optionsBuilder = new DbContextOptionsBuilder<JellyfinDbContext>();

        // Build a NpgsqlDataSource for EF Core configuration. The DI-owned singleton data source
        // is not available in design-time context; this instance is intentionally not disposed here
        // because EF Core holds a reference to it for the lifetime of the returned context.
        // As a design-time-only factory (used only for dotnet-ef CLI operations), the process
        // exits after the migration is applied, which releases all resources.
#pragma warning disable CA2000 // Dispose objects before losing scope
        var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
#pragma warning restore CA2000 // Dispose objects before losing scope
        optionsBuilder.UseNpgsql(dataSource, o => o.MigrationsAssembly(GetType().Assembly));

        return new JellyfinDbContext(
            optionsBuilder.Options,
            NullLogger<JellyfinDbContext>.Instance,
            new PostgreSqlDatabaseProvider(dataSource),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }
}
