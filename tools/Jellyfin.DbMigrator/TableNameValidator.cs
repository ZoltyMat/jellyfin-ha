using System;
using System.Text.RegularExpressions;

namespace Jellyfin.DbMigrator;

/// <summary>
/// Validates database table names to prevent SQL injection when names are
/// interpolated into raw SQL strings.
/// </summary>
internal static partial class TableNameValidator
{
    /// <summary>
    /// Gets the compiled regular expression that matches safe table names.
    /// A safe name consists only of ASCII letters, decimal digits, and underscores.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeNameRegex();

    /// <summary>
    /// Throws an <see cref="ArgumentException"/> when <paramref name="tableName"/>
    /// contains characters that are not safe to embed inside a quoted SQL identifier.
    /// </summary>
    /// <param name="tableName">The candidate table name.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="tableName"/> contains characters outside
    /// <c>[A-Za-z0-9_]</c>.
    /// </exception>
    public static void EnsureSafe(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        if (!SafeNameRegex().IsMatch(tableName))
        {
            throw new ArgumentException(
                $"Table name '{tableName}' contains characters that are not allowed in a SQL identifier.",
                nameof(tableName));
        }
    }
}
