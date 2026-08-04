using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DnnMigration.Infrastructure.Persistence;

/// <summary>
/// Constructs a <see cref="DnnDbContext"/> for the Entity Framework Core command-line tooling.
/// </summary>
/// <remarks>
/// <para>
/// The tooling needs a context instance before it can read the model, and it has to be able to obtain
/// one from THIS project alone. The alternative it would otherwise fall back on is to build the web
/// host and resolve the context from the application's own container, which inverts the layer graph the
/// solution is built to enforce and drags an entire request pipeline - bearer authentication, options
/// validation, tenant resolution, health probes - into a command whose only job is to read metadata.
/// This factory hands the tooling a context directly, so the only type in this solution it depends on is
/// <see cref="DnnDbContext"/>: no host, no container, no registration and no configuration binder.
/// </para>
/// <para>
/// It configures a provider and does nothing else. It opens no connection, issues no query, reads no
/// file and applies no migration, so a design-time command cannot reach a database as a side effect of
/// merely constructing the context. That restraint is what keeps the immutable-schema rule true from
/// this end of the assembly as well: the schema belongs to the legacy database, the baseline migration
/// is intentionally empty, and nothing here can emit a data-definition statement against a live
/// installation.
/// </para>
/// <para>
/// Accessibility mirrors the context deliberately. Both types are internal, so persistence cannot be
/// named from the layers above, and both are sealed. The constructor below is nevertheless public,
/// because the tooling activates this type by reflection through a parameterless constructor and a
/// public member on an internal type is still unreachable from outside this assembly.
/// </para>
/// </remarks>
internal sealed class DnnDbContextFactory : IDesignTimeDbContextFactory<DnnDbContext>
{
    /// <summary>
    /// The local target used only when the environment supplies no connection string.
    /// </summary>
    /// <remarks>
    /// A design-time convenience and never a deployment default. It names a developer-local instance and
    /// a database of its own, and it carries NO credential - there is no user id and no password here,
    /// and none may be added, because a connection string committed to source control is exactly the
    /// mistake this migration set out to stop repeating. Every deployment supplies the environment
    /// variable read below instead. The commands this factory exists to serve read the model rather than
    /// the database, so in normal use this value is never dialled at all: it exists so that
    /// model-inspection and migration-scaffolding commands can construct a context on a workstation that
    /// has no database configured, rather than failing before they reach the model.
    /// </remarks>
    private const string DesignTimeFallbackConnectionString =
        "Server=(localdb)\\MSSQLLocalDB;Database=DnnMigrationDesignTime;Trusted_Connection=True;TrustServerCertificate=True;";

    /// <summary>
    /// Initialises a new instance of the <see cref="DnnDbContextFactory"/> class.
    /// </summary>
    /// <remarks>
    /// Declared explicitly rather than left implicit because it is a contract with the tooling, which
    /// discovers this type by scanning the assembly and activates it through its public parameterless
    /// constructor. Nothing is injected: a design-time factory that took a dependency would need a
    /// container to satisfy it, and needing a container is the coupling this type exists to avoid.
    /// </remarks>
    public DnnDbContextFactory()
    {
    }

    /// <summary>
    /// Creates a context bound to the SQL Server provider.
    /// </summary>
    /// <param name="args">
    /// Arguments forwarded by the tooling. Deliberately unused: this factory recognises no option of its
    /// own, and a value read from here would become a second, undocumented configuration channel
    /// competing with the one key below.
    /// </param>
    /// <returns>
    /// A context whose provider is configured and whose connection has not been opened.
    /// </returns>
    public DnnDbContext CreateDbContext(string[] args)
    {
        _ = args;

        // MIGRATION: the legacy connection string was named SiteSqlServer and was reached through the
        // provider-indirection chain in Website/release.config - a default provider naming an element,
        // that element naming a configuration entry, the element's own attribute standing in when the
        // entry was empty, and the whole resolution repeated independently by each provider stack
        // (Library/Providers/DataProviders/SqlDataProvider/SqlDataProvider.vb:L61-L65 and
        // Library/Providers/MembershipProviders/DataProvider/SqlDataProvider.vb:L70-L74), with a
        // duplicate of the same value held elsewhere in the file for legacy modules. All of that
        // collapses into ONE logical key, ConnectionStrings:Default, whose container environment form is
        // exactly ConnectionStrings__Default - the name read here, and the name the compose file sets on
        // the API service.
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
