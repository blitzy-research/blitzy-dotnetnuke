using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="Module"/> aggregate root to the legacy <c>dbo.Modules</c> table.
/// </summary>
/// <remarks>
/// <para>
/// This is the mapping most distorted by the legacy code's shape. The legacy
/// <c>ModuleInfo</c> class carried fifty-eight properties because it was a flattened join across
/// <c>Modules</c>, <c>TabModules</c>, <c>ModuleDefinitions</c> and <c>ModuleControls</c>. The
/// 03.00.01 script had already split the storage: it dropped thirteen columns from this table —
/// <c>ModuleOrder</c>, <c>PaneName</c>, <c>CacheTime</c>, <c>Alignment</c>, <c>Color</c>,
/// <c>Border</c>, <c>IconFile</c>, <c>Personalize</c>, <c>ShowTitle</c>, <c>ContainerSrc</c>,
/// <c>TabID</c>, <c>AuthorizedEditRoles</c> and <c>AuthorizedViewRoles</c> — and moved the
/// placement-specific ones to <c>TabModules</c>. Binding any of them here would bind columns that
/// do not exist; they belong to <see cref="TabModule"/>.
/// </para>
/// <para>
/// The identity seed is <c>0</c>, so the first module in a fresh installation carries the
/// identifier zero. Zero is a genuine module and must never be read as "no module".
/// </para>
/// <para>
/// <c>PortalID</c> arrived late, at 03.00.01, and is nullable: a module with no tenant is a
/// host-level module. Its foreign key was declared without an <c>ON DELETE</c> clause, so removing
/// a tenant does not remove its modules at the database level. The mapping states that faithfully
/// with no delete behaviour rather than letting the optional-relationship convention null the
/// foreign key out on tracked children, which would silently convert a tenant's module into a
/// host module.
/// </para>
/// <para>
/// <c>InheritViewPermissions</c> is a nullable flag, and the distinction matters. When it is set,
/// the module's view grants come from its page rather than from its own grant rows; when it is
/// absent, the module's own grants apply. The permission service reproduces that branch exactly as
/// the legacy read path did.
/// </para>
/// </remarks>
internal sealed class ModuleConfiguration : IEntityTypeConfiguration<Module>
{
    /// <summary>
    /// Applies the mapping.
    /// </summary>
    /// <param name="builder">The builder for the module entity type.</param>
    public void Configure(EntityTypeBuilder<Module> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Modules", "dbo");

        builder.HasKey(m => m.ModuleId).HasName("PK_Modules");

        builder.Property(m => m.ModuleId)
            .HasColumnName("ModuleID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(0, 1);

        builder.Property(m => m.ModuleDefinitionId)
            .HasColumnName("ModuleDefID")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(m => m.PortalId)
            .HasColumnName("PortalID")
            .HasColumnType("int");

        builder.Property(m => m.ModuleTitle)
            .HasColumnName("ModuleTitle")
            .HasMaxLength(256);

        builder.Property(m => m.AllTabs)
            .HasColumnName("AllTabs")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(m => m.IsDeleted)
            .HasColumnName("IsDeleted")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(m => m.InheritViewPermissions)
            .HasColumnName("InheritViewPermissions")
            .HasColumnType("bit");

        // Both are ntext in the schema, so no maximum length can be declared for them.
        builder.Property(m => m.Header)
            .HasColumnName("Header")
            .HasColumnType("ntext");

        builder.Property(m => m.Footer)
            .HasColumnName("Footer")
            .HasColumnType("ntext");

        builder.Property(m => m.StartDate)
            .HasColumnName("StartDate")
            .HasColumnType("datetime");

        builder.Property(m => m.EndDate)
            .HasColumnName("EndDate")
            .HasColumnType("datetime");

        builder.HasOne(m => m.ModuleDefinition)
            .WithMany(d => d.Modules)
            .HasForeignKey(m => m.ModuleDefinitionId)
            .HasConstraintName("FK_Modules_ModuleDefinitions")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.Portal)
            .WithMany(p => p.Modules)
            .HasForeignKey(m => m.PortalId)
            .HasConstraintName("FK_Modules_Portals")
            .OnDelete(DeleteBehavior.NoAction);
    }
}
