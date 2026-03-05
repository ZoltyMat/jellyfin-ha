using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.PostgreSQL;
using Jellyfin.Database.Providers.Sqlite;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using JellyfinDbProviderFactory = System.Func<System.IServiceProvider, Jellyfin.Database.Implementations.IJellyfinDatabaseProvider>;

namespace Jellyfin.Server.Implementations.Extensions;

/// <summary>
/// Extensions for the <see cref="IServiceCollection"/> interface.
/// </summary>
public static class ServiceCollectionExtensions
{
    private static IEnumerable<Type> DatabaseProviderTypes()
    {
        yield return typeof(SqliteDatabaseProvider);
        yield return typeof(PostgreSqlDatabaseProvider);
    }

    private static int GetPoolOption(IEnumerable<CustomDatabaseOption>? options, string key, int defaultValue)
    {
        var value = options?.FirstOrDefault(o => o.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;
        return int.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private static IDictionary<string, JellyfinDbProviderFactory> GetSupportedDbProviders()
    {
        var items = new Dictionary<string, JellyfinDbProviderFactory>(StringComparer.InvariantCultureIgnoreCase);
        foreach (var providerType in DatabaseProviderTypes())
        {
            var keyAttribute = providerType.GetCustomAttribute<JellyfinDatabaseProviderKeyAttribute>();
            if (keyAttribute is null || string.IsNullOrWhiteSpace(keyAttribute.DatabaseProviderKey))
            {
                continue;
            }

            var provider = providerType;
            items[keyAttribute.DatabaseProviderKey] = (services) => (IJellyfinDatabaseProvider)ActivatorUtilities.CreateInstance(services, providerType);
        }

        return items;
    }

    private static JellyfinDbProviderFactory? LoadDatabasePlugin(CustomDatabaseOptions customProviderOptions, IApplicationPaths applicationPaths)
    {
        var plugin = Directory.EnumerateDirectories(applicationPaths.PluginsPath)
            .Where(e => Path.GetFileName(e)!.StartsWith(customProviderOptions.PluginName, StringComparison.OrdinalIgnoreCase))
            .Order()
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"The requested custom database plugin with the name '{customProviderOptions.PluginName}' could not been found in '{applicationPaths.PluginsPath}'");

        var dbProviderAssembly = Path.Combine(plugin, Path.ChangeExtension(customProviderOptions.PluginAssembly, "dll"));
        if (!File.Exists(dbProviderAssembly))
        {
            throw new InvalidOperationException($"Could not find the requested assembly at '{dbProviderAssembly}'");
        }

        // we have to load the assembly without proxy to ensure maximum performance for this.
        var assembly = Assembly.LoadFrom(dbProviderAssembly);
        var dbProviderType = assembly.GetExportedTypes().FirstOrDefault(f => f.IsAssignableTo(typeof(IJellyfinDatabaseProvider)))
            ?? throw new InvalidOperationException($"Could not find any type implementing the '{nameof(IJellyfinDatabaseProvider)}' interface.");

        return (services) => (IJellyfinDatabaseProvider)ActivatorUtilities.CreateInstance(services, dbProviderType);
    }

    /// <summary>
    /// Adds the <see cref="IDbContextFactory{TContext}"/> interface to the service collection with second level caching enabled.
    /// </summary>
    /// <param name="serviceCollection">An instance of the <see cref="IServiceCollection"/> interface.</param>
    /// <param name="configurationManager">The server configuration manager.</param>
    /// <param name="configuration">The startup Configuration.</param>
    /// <returns>The updated service collection.</returns>
    public static IServiceCollection AddJellyfinDbContext(
        this IServiceCollection serviceCollection,
        IServerConfigurationManager configurationManager,
        IConfiguration configuration)
    {
        var efCoreConfiguration = configurationManager.GetConfiguration<DatabaseConfigurationOptions>("database");
        JellyfinDbProviderFactory? providerFactory = null;

        if (efCoreConfiguration?.DatabaseType is null)
        {
            var cmdMigrationArgument = configuration.GetValue<string>("migration-provider");
            if (!string.IsNullOrWhiteSpace(cmdMigrationArgument))
            {
                efCoreConfiguration = new DatabaseConfigurationOptions()
                {
                    DatabaseType = cmdMigrationArgument,
                };
            }
            else
            {
                // when nothing is setup via new Database configuration, fallback to SQLite with default settings.
                efCoreConfiguration = new DatabaseConfigurationOptions()
                {
                    DatabaseType = "Jellyfin-SQLite",
                    LockingBehavior = DatabaseLockingBehaviorTypes.NoLock
                };
                configurationManager.SaveConfiguration("database", efCoreConfiguration);
            }
        }

        if (efCoreConfiguration.DatabaseType.Equals("PLUGIN_PROVIDER", StringComparison.OrdinalIgnoreCase))
        {
            if (efCoreConfiguration.CustomProviderOptions is null)
            {
                throw new InvalidOperationException("The custom database provider must declare the custom provider options to work");
            }

            providerFactory = LoadDatabasePlugin(efCoreConfiguration.CustomProviderOptions, configurationManager.ApplicationPaths);
        }
        else
        {
            var providers = GetSupportedDbProviders();
            if (!providers.TryGetValue(efCoreConfiguration.DatabaseType.ToUpperInvariant(), out providerFactory!))
            {
                throw new InvalidOperationException($"Jellyfin cannot find the database provider of type '{efCoreConfiguration.DatabaseType}'. Supported types are {string.Join(", ", providers.Keys)}");
            }
        }

        serviceCollection.AddSingleton<IJellyfinDatabaseProvider>(providerFactory!);

        if (efCoreConfiguration.DatabaseType.Equals("Jellyfin-PostgreSQL", StringComparison.OrdinalIgnoreCase))
        {
            serviceCollection.AddSingleton<NpgsqlDataSource>(static sp =>
            {
                var config = sp.GetRequiredService<IServerConfigurationManager>().GetConfiguration<DatabaseConfigurationOptions>("database");
                var options = config.CustomProviderOptions?.Options;

                var connectionString =
                    Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
                    ?? options
                        ?.FirstOrDefault(o => o.Key.Equals("ConnectionString", StringComparison.OrdinalIgnoreCase))
                        ?.Value
                    ?? config.CustomProviderOptions?.ConnectionString
                    ?? throw new InvalidOperationException(
                        "No PostgreSQL connection string found. Set the POSTGRES_CONNECTION_STRING environment variable, " +
                        "or provide it via CustomProviderOptions.Options[\"ConnectionString\"] or CustomProviderOptions.ConnectionString.");

                // Support postgresql:// / postgres:// URI format (e.g. DATABASE_URL convention).
                // NpgsqlDataSourceBuilder requires ADO.NET key=value format; convert if needed.
                if (connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)
                    || connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
                {
                    var uri = new Uri(connectionString);
                    var userInfoParts = uri.UserInfo.Split(':', 2);
                    connectionString = new NpgsqlConnectionStringBuilder
                    {
                        Host = uri.Host,
                        Port = uri.Port > 0 ? uri.Port : 5432,
                        Database = uri.AbsolutePath.TrimStart('/'),
                        Username = userInfoParts.Length > 0 ? Uri.UnescapeDataString(userInfoParts[0]) : null,
                        Password = userInfoParts.Length > 1 ? Uri.UnescapeDataString(userInfoParts[1]) : null,
                    }.ToString();
                }

                var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);

                dataSourceBuilder.ConnectionStringBuilder.MinPoolSize = GetPoolOption(options, "MinPoolSize", 2);
                dataSourceBuilder.ConnectionStringBuilder.MaxPoolSize = GetPoolOption(options, "MaxPoolSize", 20);
                dataSourceBuilder.ConnectionStringBuilder.CommandTimeout = GetPoolOption(options, "CommandTimeout", 30);

                return dataSourceBuilder.Build();
            });
        }

        switch (efCoreConfiguration.LockingBehavior)
        {
            case DatabaseLockingBehaviorTypes.NoLock:
                serviceCollection.AddSingleton<IEntityFrameworkCoreLockingBehavior, NoLockBehavior>();
                break;
            case DatabaseLockingBehaviorTypes.Pessimistic:
                serviceCollection.AddSingleton<IEntityFrameworkCoreLockingBehavior, PessimisticLockBehavior>();
                break;
            case DatabaseLockingBehaviorTypes.Optimistic:
                serviceCollection.AddSingleton<IEntityFrameworkCoreLockingBehavior, OptimisticLockBehavior>();
                break;
        }

        serviceCollection.AddPooledDbContextFactory<JellyfinDbContext>((serviceProvider, opt) =>
        {
            var provider = serviceProvider.GetRequiredService<IJellyfinDatabaseProvider>();
            provider.Initialise(opt, efCoreConfiguration);
            var lockingBehavior = serviceProvider.GetRequiredService<IEntityFrameworkCoreLockingBehavior>();
            lockingBehavior.Initialise(opt);
        });

        return serviceCollection;
    }
}
