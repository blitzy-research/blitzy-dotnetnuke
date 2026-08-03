using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace DnnMigration.Infrastructure.Persistence;

/// <summary>
/// The Entity Framework context bound to the existing DotNetNuke SQL Server schema.
/// </summary>
/// <remarks>
/// <para>
/// This type replaces two legacy layers at once: the abstract data provider that declared two
/// hundred and sixty-nine overridable members behind a reflection-instantiated singleton, and the
/// concrete provider that reached the database by concatenating a database owner, an object
/// qualifier and a stored procedure name and handing the result to a helper type whose only artefact
/// in the repository is a compiled assembly with no source. Neither is ported; both are replaced by
/// the sets below and the LINQ the repositories write against them.
/// </para>
/// <para>
/// The context is deliberately schema-following rather than schema-owning. Every mapping is stated
/// explicitly in an <see cref="IEntityTypeConfiguration{TEntity}"/> under
/// <c>Persistence/Configurations</c>, bound to the terminal state of the eighty-eight script upgrade
/// chain, and the baseline migration is intentionally empty. No table is created, altered or dropped
/// by anything in this assembly. That is not a convenience: the terminal schema depends on
/// membership objects that the upgrade scripts only ever <c>ALTER</c>, never create, because they are
/// installed by an external tool. A generated create-migration therefore cannot reproduce the schema
/// even in principle, and <c>EnsureCreated</c> must never be called against a real database.
/// </para>
/// <para>
/// The type is public even though nothing outside this assembly should reach the database through it.
/// The architectural guarantee that no business logic touches a context is enforced by the project
/// reference graph rather than by accessibility: the application layer does not reference this
/// assembly at all and so cannot name this type whatever its accessibility. Public accessibility is
/// what lets the design-time factory and the integration-test host construct and reconfigure the
/// context without an assembly-visibility escape hatch.
/// </para>
/// <para>
/// Configurations are discovered by assembly scan rather than registered one by one. The scan finds
/// internal configuration classes, which is why every configuration in this assembly is
/// <c>internal sealed</c>, and it means adding an entity cannot be half-done: a configuration that
/// exists is applied, and an entity without one fails model validation loudly rather than silently
/// binding to a conventional table name that does not exist.
/// </para>
/// </remarks>
internal sealed class DnnDbContext : DbContext
{
    /// <summary>
    /// Initialises a new instance of the <see cref="DnnDbContext"/> class.
    /// </summary>
    /// <param name="options">The options supplied by the container or the design-time factory.</param>
    public DnnDbContext(DbContextOptions<DnnDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Gets or sets a value indicating whether the unit of work is holding a transaction it opened itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THIS IS A PLAIN FIELD AND NOT A QUESTION ASKED OF THE DATABASE FACADE. The registered execution
    /// strategy factory has to know the answer, because a RETRYING strategy refuses to run inside a
    /// caller-opened transaction and must be substituted for a non-retrying one while a scope is held. The
    /// obvious way to find out - reading <c>Database.CurrentTransaction</c> from inside the factory - CANNOT
    /// be used: that property resolves the facade's dependency bundle, and the bundle includes the execution
    /// strategy factory itself, so the factory ends up asking a question whose answer requires the factory.
    /// The result is not an exception but a HANG, which was observed rather than predicted: an integration
    /// run stopped after fixture start-up with no active request and no open transaction on the server at
    /// all. A field the factory can read without resolving anything removes the cycle rather than
    /// documenting a way to live near it.
    /// </para>
    /// <para>
    /// Set by the unit of work when it opens a scope and cleared when the scope is disposed, which is the
    /// only code permitted to touch it. It is not a substitute for
    /// <c>Database.CurrentTransaction</c> anywhere else - callers that need to know whether a transaction is
    /// open should still ask the facade, which is authoritative and safe to ask from outside the factory.
    /// </para>
    /// </remarks>
    public bool ExplicitTransactionOpen { get; set; }

    /// <summary>Gets the tenant containers.</summary>
    public DbSet<Portal> Portals => Set<Portal>();

    /// <summary>Gets the host names each tenant answers on.</summary>
    public DbSet<PortalAlias> PortalAliases => Set<PortalAlias>();

    /// <summary>Gets the installed module packages.</summary>
    public DbSet<DesktopModule> DesktopModules => Set<DesktopModule>();

    /// <summary>Gets the grants of a package to a tenant.</summary>
    public DbSet<PortalDesktopModule> PortalDesktopModules => Set<PortalDesktopModule>();

    /// <summary>Gets the definitions a package exposes.</summary>
    public DbSet<ModuleDefinition> ModuleDefinitions => Set<ModuleDefinition>();

    /// <summary>Gets the user-interface entry points a definition offers.</summary>
    public DbSet<ModuleControl> ModuleControls => Set<ModuleControl>();

    /// <summary>Gets the module instances.</summary>
    public DbSet<Module> Modules => Set<Module>();

    /// <summary>Gets the settings held against a module instance.</summary>
    public DbSet<ModuleSetting> ModuleSettings => Set<ModuleSetting>();

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

    /// <summary>Gets the profile property definitions.</summary>
    public DbSet<ProfilePropertyDefinition> ProfilePropertyDefinitions => Set<ProfilePropertyDefinition>();

    /// <summary>Gets the stored profile values.</summary>
    public DbSet<UserProfileValue> UserProfileValues => Set<UserProfileValue>();

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
    /// Removes the conventions that would add model artefacts the existing schema does not contain.
    /// </summary>
    /// <param name="configurationBuilder">The convention and type-mapping configuration builder.</param>
    /// <remarks>
    /// <para>
    /// <c>ForeignKeyIndexConvention</c> creates a covering index for every foreign key that is not
    /// already the leading portion of some other index. That is sound advice for a schema Entity
    /// Framework Core owns, and wrong for this one: Rule T4 makes the existing DotNetNuke database the
    /// authority, and its terminal state carries exactly thirty-three non-primary-key indexes, every
    /// one of which is declared explicitly in <c>Persistence/Configurations</c> against the upgrade
    /// script that created it. Leaving the convention in place added eight more that no script ever
    /// created - over <c>Modules</c>, <c>Permission</c>, <c>PortalAlias</c>,
    /// <c>PortalDesktopModules</c>, <c>ProfilePropertyDefinition</c>, <c>Roles</c>,
    /// <c>TabModules</c> and <c>UserProfile</c> - so the model described forty-one indexes against a
    /// database holding thirty-three.
    /// </para>
    /// <para>
    /// The consequence was not cosmetic. The model snapshot is what a future migration is diffed
    /// against, so a convention-invented index is indistinguishable from one the database really has:
    /// a later scaffold would either propose dropping indexes that were never created or silently
    /// treat them as present. Removing the convention makes the explicit declarations the only source
    /// of index metadata, which is the only arrangement under which the model can be compared to the
    /// real schema and believed.
    /// </para>
    /// <para>
    /// Nothing else is removed. Every other convention either has no effect on a fully explicit
    /// configuration or supplies behaviour the configurations rely on, and removing conventions
    /// wholesale would trade one class of invented metadata for another.
    /// </para>
    /// </remarks>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        configurationBuilder.Conventions.Remove(typeof(ForeignKeyIndexConvention));

        base.ConfigureConventions(configurationBuilder);
    }

    /// <summary>
    /// Applies every entity configuration declared in this assembly.
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DnnDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
