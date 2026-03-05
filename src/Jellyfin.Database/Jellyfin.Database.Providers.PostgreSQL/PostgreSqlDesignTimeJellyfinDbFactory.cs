using System;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging.Abstractions;

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
        optionsBuilder.UseNpgsql(connectionString, o => o.MigrationsAssembly(GetType().Assembly));

        return new JellyfinDbContext(
            optionsBuilder.Options,
            NullLogger<JellyfinDbContext>.Instance,
            new PostgreSqlDatabaseProvider(),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }
}
