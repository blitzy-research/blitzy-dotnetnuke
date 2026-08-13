using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DnnMigration.Infrastructure.Persistence;

/// <summary>Constructs a <see cref="DnnDbContext"/> for the Entity Framework Core command-line tooling.</summary>
/// <remarks>
/// <para>
/// The tooling needs a context instance before it can read the model, and it has to be able to obtain one
/// from THIS project alone.
/// </para>
/// <para>
/// Accessibility mirrors the context deliberately. Both types are internal, so persistence cannot be named
/// from the layers above, and both are sealed.
/// </para>
/// </remarks>
internal sealed class DnnDbContextFactory : IDesignTimeDbContextFactory<DnnDbContext>
{
    /// <summary>The local target used only when the environment supplies no connection string.</summary>
    /// <remarks>
    /// A design-time convenience and never a deployment default. It names a developer-local instance and a
    /// database of its own, and it carries NO credential - there is no user id and no password here, and
    /// none may be added, because a connection string committed to source control is exactly the mistake
    /// this migration set out to stop repeating.
    /// </remarks>
    private const string DesignTimeFallbackConnectionString =
        "Server=(localdb)\\MSSQLLocalDB;Database=DnnMigrationDesignTime;Trusted_Connection=True;TrustServerCertificate=True;";

    /// <summary>Initialises a new instance of the <see cref="DnnDbContextFactory"/> class.</summary>
    /// <remarks>
    /// Declared explicitly rather than left implicit because it is a contract with the tooling, which
    /// discovers this type by scanning the assembly and activates it through its public parameterless
    /// constructor. Nothing is injected: a design-time factory that took a dependency would need a
    /// container to satisfy it, and needing a container is the coupling this type exists to avoid.
    /// </remarks>
    public DnnDbContextFactory()
    {
    }

    /// <summary>Creates a context bound to the SQL Server provider.</summary>
    /// <param name="args">Arguments forwarded by the tooling.</param>
    /// <returns>A context whose provider is configured and whose connection has not been opened.</returns>
    public DnnDbContext CreateDbContext(string[] args)
    {
        _ = args;

        string? configured = Environment.GetEnvironmentVariable("ConnectionStrings__Default");

        string connectionString = string.IsNullOrWhiteSpace(configured)
            ? DesignTimeFallbackConnectionString
            : configured;

        DbContextOptions<DnnDbContext> options = new DbContextOptionsBuilder<DnnDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new DnnDbContext(options);
    }
}
