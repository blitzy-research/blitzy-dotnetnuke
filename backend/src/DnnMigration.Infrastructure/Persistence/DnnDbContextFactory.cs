using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DnnMigration.Infrastructure.Persistence;

/// <summary>
/// Constructs a <see cref="DnnDbContext"/> for the Entity Framework command-line tooling.
/// </summary>
/// <remarks>
/// <para>
/// The tooling needs a context instance to read the model, and it cannot build one through the
/// application's own container because doing so would start the web host. This factory gives it one
/// directly.
/// </para>
/// <para>
/// The connection string is read from the <c>ConnectionStrings__Default</c> environment variable —
/// the same name the container composition supplies — so that a design-time operation which really
/// does touch a database uses the same target as the running application. When the variable is
/// absent, a deliberately unreachable placeholder is used: every operation this factory exists to
/// support reads the model rather than the database, and a placeholder that cannot connect is safer
/// than one that might connect to something unintended. No credential appears here or anywhere else
/// in the source.
/// </para>
/// <para>
/// The baseline migration this factory scaffolds and applies is intentionally empty, so applying it
/// records a version row and touches nothing else. Nothing in this assembly may be used to create or
/// alter a production table.
/// </para>
/// </remarks>
internal sealed class DnnDbContextFactory : IDesignTimeDbContextFactory<DnnDbContext>
{
    /// <summary>
    /// The environment variable the running application and the container composition both use.
    /// </summary>
    private const string ConnectionStringVariable = "ConnectionStrings__Default";

    /// <summary>
    /// A syntactically valid connection string that resolves to no reachable server, used when no
    /// environment variable is present.
    /// </summary>
    private const string DesignTimePlaceholder =
        "Server=design-time;Database=DotNetNuke;Trusted_Connection=True;TrustServerCertificate=True";

    /// <summary>
    /// Creates the context.
    /// </summary>
    /// <param name="args">Arguments passed by the tooling; unused.</param>
    /// <returns>A context bound to the SQL Server provider.</returns>
    public DnnDbContext CreateDbContext(string[] args)
    {
        string connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable) is { Length: > 0 } configured
                ? configured
                : DesignTimePlaceholder;

        DbContextOptionsBuilder<DnnDbContext> options = new();
        options.UseSqlServer(
            connectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"));

        return new DnnDbContext(options.Options);
    }
}
