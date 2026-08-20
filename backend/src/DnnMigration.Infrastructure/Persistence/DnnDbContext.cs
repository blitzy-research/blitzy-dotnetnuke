using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace DnnMigration.Infrastructure.Persistence;

/// <summary>The Entity Framework Core context bound to the existing DotNetNuke SQL Server schema.</summary>
/// <remarks>
/// <para>
/// The context is deliberately schema-following rather than schema-owning. Every mapping is stated
/// explicitly in an <see cref="IEntityTypeConfiguration{TEntity}"/> under
/// <c>Persistence/Configurations</c>, bound to the terminal state of the eighty-eight script upgrade chain,
/// and the baseline migration is intentionally empty.
/// </para>
/// <para>
/// The type is <see langword="internal"/> and sealed, which is the accessibility half of the rule that
/// keeps persistence out of the layers above it. The application layer reaches the database only through
/// the repository and unit-of-work abstractions the domain declares, and it cannot name this type even by
/// accident.
/// </para>
/// </remarks>
internal sealed class DnnDbContext : DbContext
{
    /// <summary>Initialises a new instance of the <see cref="DnnDbContext"/> class.</summary>
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
    /// WHY THIS MEMBER EXISTS AT ALL, ON A TYPE THAT OTHERWISE CARRIES ONLY SETS. It is not unit-of-work
    /// behaviour and it opens, commits and rolls back nothing: it is one bit of persistence-mechanism state
    /// parked on the only object the execution strategy can reach.
    /// </remarks>
    public bool ExplicitTransactionOpen { get; set; }

    /// <summary>Removes the convention that would add model artefacts the existing schema does not contain.</summary>
    /// <param name="configurationBuilder">The convention and type-mapping configuration builder.</param>
    /// <remarks>
    /// The consequence was not cosmetic. The model snapshot is what a future migration is diffed against,
    /// so a convention-invented index is indistinguishable from one the database really has: a later
    /// scaffold would either propose dropping indexes that were never created or silently treat them as
    /// present.
    /// </remarks>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        base.ConfigureConventions(configurationBuilder);

        configurationBuilder.Conventions.Remove(typeof(ForeignKeyIndexConvention));
    }

    /// <summary>Applies every entity configuration declared in this assembly.</summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <remarks>
    /// The assembly scan is the whole body on purpose. A mapping written here would compete with the
    /// configuration that owns the same entity, and the last writer would win silently, so the twenty-one
    /// configurations under <c>Persistence/Configurations</c> remain the single place a table or column
    /// name is stated.
    /// </remarks>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DnnDbContext).Assembly);
    }
}
