using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace DnnMigration.Infrastructure.Persistence;

/// <summary>
/// The Entity Framework Core context bound to the existing DotNetNuke SQL Server schema.
/// </summary>
/// <remarks>
/// <para>
/// This type replaces two legacy layers at once: the abstract data provider that declared two hundred
/// and sixty-nine overridable members behind a reflection-instantiated singleton, and the concrete
/// provider that reached the database by concatenating a database owner, an object qualifier and a
/// stored procedure name and handing the result to a helper type whose only artefact in the repository
/// is a compiled assembly with no source. Neither is ported; both are replaced by the sets below and
/// the LINQ the repositories write against them. Nothing here hydrates a row by reflection or reads an
/// <c>IDataReader</c> column by column, because the materialiser does that work.
/// </para>
/// <para>
/// The context is deliberately schema-following rather than schema-owning. Every mapping is stated
/// explicitly in an <see cref="IEntityTypeConfiguration{TEntity}"/> under
/// <c>Persistence/Configurations</c>, bound to the terminal state of the eighty-eight script upgrade
/// chain, and the baseline migration is intentionally empty. No table is created, altered or dropped by
/// anything in this assembly. That is not a convenience: the terminal schema depends on membership
/// objects that the upgrade scripts only ever <c>ALTER</c>, never create, because they are installed by
/// an external tool. A generated create-migration therefore cannot reproduce the schema even in
/// principle, and <c>EnsureCreated</c> must never be called against a real database.
/// </para>
/// <para>
/// The type is <see langword="internal"/> and sealed, which is the accessibility half of the rule that
/// keeps persistence out of the layers above it. The application layer reaches the database only through
/// the repository and unit-of-work abstractions the domain declares, and it cannot name this type even
/// by accident. Sealing it removes the other escape route: no derived context can widen the surface or
/// reintroduce mapping that belongs in a configuration. The single constructor is public so that the
/// container registration and the design-time factory — both of which live in this assembly — can
/// construct it without an assembly-visibility escape hatch; a public member on an internal type is
/// still unreachable from outside.
/// </para>
/// <para>
/// Configurations are discovered by assembly scan rather than registered one by one. The scan finds the
/// internal configuration classes, which is why every configuration in this assembly is
/// <c>internal sealed</c>, and it means adding an entity cannot be half-done: a configuration that
/// exists is applied, and an entity without one fails model validation loudly rather than silently
/// binding to a conventional table name that does not exist.
/// </para>
/// </remarks>
internal sealed class DnnDbContext : DbContext
{
    // MIGRATION: this scoped context replaces the reflection-resolved DataProvider singleton and its
    // SqlHelper-based stored-procedure surface. It MAPS the existing DotNetNuke schema and nothing
    // more: it never creates, alters or migrates that schema at runtime, so no data-definition
    // language, connection string, object-qualifier logic or table mapping belongs in this file. Table
    // and column names live exclusively in Persistence/Configurations, and the baseline migration is
    // empty by design.

    /// <summary>
    /// Initialises a new instance of the <see cref="DnnDbContext"/> class.
    /// </summary>
    /// <param name="options">The options supplied by the container or the design-time factory.</param>
    public DnnDbContext(DbContextOptions<DnnDbContext> options)
        : base(options)
    {
    }

    /// <summary>Gets the tenant containers.</summary>
    public DbSet<Portal> Portals => Set<Portal>();

    /// <summary>Gets the host names each tenant answers on.</summary>
    public DbSet<PortalAlias> PortalAliases => Set<PortalAlias>();

    /// <summary>Gets the module instances.</summary>
    public DbSet<Module> Modules => Set<Module>();

    /// <summary>Gets the definitions a module package exposes.</summary>
    public DbSet<ModuleDefinition> ModuleDefinitions => Set<ModuleDefinition>();

    /// <summary>Gets the user-interface entry points a definition offers.</summary>
    public DbSet<ModuleControl> ModuleControls => Set<ModuleControl>();

    /// <summary>Gets the settings held against a module instance.</summary>
    public DbSet<ModuleSetting> ModuleSettings => Set<ModuleSetting>();

    /// <summary>Gets the installed module packages.</summary>
    public DbSet<DesktopModule> DesktopModules => Set<DesktopModule>();

    /// <summary>Gets the grants of a module package to a tenant.</summary>
    public DbSet<PortalDesktopModule> PortalDesktopModules => Set<PortalDesktopModule>();

    /// <summary>Gets the pages.</summary>
    public DbSet<Tab> Tabs => Set<Tab>();

    /// <summary>Gets the placements of a module on a page.</summary>
    public DbSet<TabModule> TabModules => Set<TabModule>();

    /// <summary>Gets the settings held against a placement.</summary>
    public DbSet<TabModuleSetting> TabModuleSettings => Set<TabModuleSetting>();

    /// <summary>Gets the accounts.</summary>
    public DbSet<User> Users => Set<User>();

    /// <summary>Gets the memberships that place an account in a tenant.</summary>
    public DbSet<UserPortal> UserPortals => Set<UserPortal>();

    /// <summary>Gets the stored profile values.</summary>
    public DbSet<UserProfileValue> UserProfileValues => Set<UserProfileValue>();

    /// <summary>Gets the profile property definitions.</summary>
    public DbSet<ProfilePropertyDefinition> ProfilePropertyDefinitions => Set<ProfilePropertyDefinition>();

    /// <summary>Gets the roles.</summary>
    public DbSet<Role> Roles => Set<Role>();

    /// <summary>Gets the organisational groupings of roles.</summary>
    public DbSet<RoleGroup> RoleGroups => Set<RoleGroup>();

    /// <summary>Gets the assignments of an account to a role.</summary>
    public DbSet<UserRole> UserRoles => Set<UserRole>();

    /// <summary>Gets the catalogue of recognised permission keys.</summary>
    public DbSet<Permission> Permissions => Set<Permission>();

    /// <summary>Gets the grants held against a module.</summary>
    public DbSet<ModulePermission> ModulePermissions => Set<ModulePermission>();

    /// <summary>Gets the grants held against a page.</summary>
    public DbSet<TabPermission> TabPermissions => Set<TabPermission>();

    /// <summary>
    /// Gets or sets a value indicating whether the unit of work is holding a transaction it opened itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THIS MEMBER EXISTS AT ALL, ON A TYPE THAT OTHERWISE CARRIES ONLY SETS. It is not unit-of-work
    /// behaviour and it opens, commits and rolls back nothing: it is one bit of persistence-mechanism
    /// state parked on the only object the execution strategy can reach. The registered strategy factory
    /// has to know whether a caller-opened transaction is active, because the provider's RETRYING
    /// strategy refuses to run inside one and must stand down while a scope is held, and the strategy
    /// sees the world exclusively through <c>Dependencies.CurrentContext.Context</c>.
    /// </para>
    /// <para>
    /// The obvious way to find out — reading <c>Database.CurrentTransaction</c> from inside the factory —
    /// CANNOT be used: that property resolves the facade's dependency bundle, and the bundle includes the
    /// execution strategy factory itself, so the factory ends up asking a question whose answer requires
    /// the factory. The result is not an exception but a HANG, which was observed rather than predicted:
    /// an integration run stopped after fixture start-up with no active request and no open transaction
    /// on the server at all. A field the strategy can read without resolving anything removes the cycle
    /// rather than documenting a way to live near it.
    /// </para>
    /// <para>
    /// Set by the unit of work when it opens a scope and cleared when the scope is disposed, which is the
    /// only code permitted to touch it. It is not a substitute for <c>Database.CurrentTransaction</c>
    /// anywhere else — callers that need to know whether a transaction is open should still ask the
    /// facade, which is authoritative and safe to ask from outside the factory. Do not delete it to slim
    /// the context down: <c>UnitOfWork</c> and <c>TransactionAwareExecutionStrategy</c> both bind to it,
    /// and without it the multi-commit tenant writes lose either their atomicity or their transient-fault
    /// resilience.
    /// </para>
    /// </remarks>
    public bool ExplicitTransactionOpen { get; set; }

    /// <summary>
    /// Removes the convention that would add model artefacts the existing schema does not contain.
    /// </summary>
    /// <param name="configurationBuilder">The convention and type-mapping configuration builder.</param>
    /// <remarks>
    /// <para>
    /// This override exists to ENFORCE the immutable-schema rule, not to configure anything. No
    /// convention is added and no type mapping is declared here.
    /// <c>ForeignKeyIndexConvention</c> creates a covering index for every foreign key that is not
    /// already the leading portion of some other index. That is sound advice for a schema Entity
    /// Framework Core owns, and wrong for this one: the existing DotNetNuke database is the authority,
    /// and its terminal state carries exactly thirty-three non-primary-key indexes, every one of which is
    /// declared explicitly in <c>Persistence/Configurations</c> against the upgrade script that created
    /// it. Leaving the convention in place added eight more that no script ever created — over
    /// <c>Modules</c>, <c>Permission</c>, <c>PortalAlias</c>, <c>PortalDesktopModules</c>,
    /// <c>ProfilePropertyDefinition</c>, <c>Roles</c>, <c>TabModules</c> and <c>UserProfile</c> — so the
    /// model described forty-one indexes against a database holding thirty-three.
    /// </para>
    /// <para>
    /// The consequence was not cosmetic. The model snapshot is what a future migration is diffed against,
    /// so a convention-invented index is indistinguishable from one the database really has: a later
    /// scaffold would either propose dropping indexes that were never created or silently treat them as
    /// present. Removing the convention makes the explicit declarations the only source of index
    /// metadata, which is the only arrangement under which the model can be compared to the real schema
    /// and believed. No test asserts this, so the divergence would be silent.
    /// </para>
    /// <para>
    /// Nothing else is removed. Every other convention either has no effect on a fully explicit
    /// configuration or supplies behaviour the configurations rely on, and removing conventions wholesale
    /// would trade one class of invented metadata for another.
    /// </para>
    /// </remarks>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        base.ConfigureConventions(configurationBuilder);

        configurationBuilder.Conventions.Remove(typeof(ForeignKeyIndexConvention));
    }

    /// <summary>
    /// Applies every entity configuration declared in this assembly.
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <remarks>
    /// The assembly scan is the whole body on purpose. A mapping written here would compete with the
    /// configuration that owns the same entity, and the last writer would win silently, so the twenty-one
    /// configurations under <c>Persistence/Configurations</c> remain the single place a table or column
    /// name is stated.
    /// </remarks>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DnnDbContext).Assembly);
    }
}
