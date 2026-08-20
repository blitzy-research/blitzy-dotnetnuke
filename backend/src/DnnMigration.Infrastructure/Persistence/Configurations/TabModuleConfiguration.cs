using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>Binds the <see cref="TabModule"/> entity to the existing <c>dbo.TabModules</c> table.</summary>
/// <remarks>
/// <para>
/// The schema is immutable for this migration. Every declaration below describes the terminal state of the
/// 87-script upgrade chain under <c>Website/Providers/DataProviders/SqlDataProvider/</c> as it already
/// stands; none of it creates, alters or drops anything, and the baseline migration is intentionally empty.
/// </para>
/// <para>
/// The unique index <c>IX_TabModules</c> over <c>(TabID, ModuleID)</c> is the core semantic of the table: a
/// module instance appears at most once on any given page, while still appearing on as many pages as
/// required. Both foreign keys are required and both cascade, exactly as the database declares them.
/// </para>
/// </remarks>
internal sealed class TabModuleConfiguration : IEntityTypeConfiguration<TabModule>
{
    /// <summary>Applies the mapping for the placement entity type.</summary>
    /// <param name="builder">The builder for <see cref="TabModule"/>.</param>
    /// <exception cref="ArgumentNullException"><c>builder</c> is <see langword="null"/>.</exception>
    public void Configure(EntityTypeBuilder<TabModule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TabModules", "dbo");

        // 03.00.01:L36-40 - ALTER TABLE TabModules ADD CONSTRAINT PK_TabModules PRIMARY KEY CLUSTERED
        // (TabModuleID), the constraint name at line 37. Named explicitly so the model carries the name the
        // database already has rather than one invented by convention.
        builder.HasKey(t => t.TabModuleId).HasName("PK_TabModules");

        // int NOT NULL IDENTITY (1, 1) [03.00.01:L21]. The seed is preserved rather than left to the
        // provider default, so the model states the same starting point the database does.
        builder.Property(t => t.TabModuleId)
            .HasColumnName("TabModuleID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // int NOT NULL [03.00.01:L22] - the page half of the placement, and the foreign key
        // FK_TabModules_Tabs configured below. Required, with no default constraint in the schema and
        // therefore none recorded here.
        builder.Property(t => t.TabId)
            .HasColumnName("TabID")
            .HasColumnType("int")
            .IsRequired();

        // int NOT NULL [03.00.01:L23] - the instance half of the placement, and the foreign key
        // FK_TabModules_Modules configured below. Required, and likewise carrying no default.
        builder.Property(t => t.ModuleId)
            .HasColumnName("ModuleID")
            .HasColumnType("int")
            .IsRequired();

        // nvarchar(50) NOT NULL [03.00.01:L24] - the only non-nullable string on this table. The width is
        // declared so an over-long pane name is rejected here rather than truncated by the provider.
        builder.Property(t => t.PaneName)
            .HasColumnName("PaneName")
            .HasMaxLength(50)
            .IsRequired();

        // int NOT NULL [03.00.01:L25] - the ordinal within the pane, not within the page.
        builder.Property(t => t.ModuleOrder)
            .HasColumnName("ModuleOrder")
            .HasColumnType("int")
            .IsRequired();

        // int NOT NULL [03.00.01:L26] - the output cache window in minutes, zero meaning uncached. Zero is
        // a real value here rather than an absence, so the column stays non-nullable and takes no default.
        builder.Property(t => t.CacheTime)
            .HasColumnName("CacheTime")
            .HasColumnType("int")
            .IsRequired();

        // nvarchar(10) NULL [03.00.01:L27]. Nullable, so deliberately no IsRequired: a database null
        // materialises as a CLR null and means the placement expresses no preference.
        builder.Property(t => t.Alignment)
            .HasColumnName("Alignment")
            .HasMaxLength(10);

        // nvarchar(20) NULL [03.00.01:L28]. An opaque legacy colour token carried through unchanged rather
        // than parsed, because the store admits anything twenty characters or shorter and this migration
        // does not narrow an existing contract.
        builder.Property(t => t.Color)
            .HasColumnName("Color")
            .HasMaxLength(20);

        // Nvarchar(1) NULL [03.00.01:L29] - a UNICODE column one character wide, not ANSI char(1). The
        // width is declared verbatim rather than rounded up, and no ANSI override is applied here or
        // anywhere else in this file, because this table has no non-Unicode column at all.
        builder.Property(t => t.Border)
            .HasColumnName("Border")
            .HasMaxLength(1);

        // nvarchar(100) NULL [03.00.01:L30] - a portal-relative icon path for this placement alone.
        builder.Property(t => t.IconFile)
            .HasColumnName("IconFile")
            .HasMaxLength(100);

        // Visibility stores the legacy ModuleVisibility ORDINALS - Maximized as 0, Minimized as 1, None as
        // 2 - directly in the existing int NOT NULL column [03.00.01:L31], and NO value conversion is
        // applied.
        PropertyBuilder<ModuleVisibility> visibility = builder.Property(t => t.Visibility);

        visibility
            .HasColumnName("Visibility")
            .HasColumnType("int")
            .IsRequired();

        // nvarchar(200) NULL [03.00.01:L32] - the container skin drawn around the placement. Null
        // means it inherits whatever the page or portal specifies.
        builder.Property(t => t.ContainerSrc)
            .HasColumnName("ContainerSrc")
            .HasMaxLength(200);

        builder.Property(t => t.DisplayTitle)
            .HasColumnName("DisplayTitle")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(true)
            .ValueGeneratedNever();

        builder.Property(t => t.DisplayPrint)
            .HasColumnName("DisplayPrint")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(true)
            .ValueGeneratedNever();

        // MIGRATION: DELIBERATE, DOCUMENTED DIVERGENCE, and the one member of the three flags where the
        // legacy object default and the database default genuinely disagree.
        builder.Property(t => t.DisplaySyndicate)
            .HasColumnName("DisplaySyndicate")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(true)
            .ValueGeneratedNever();

        // ALTER TABLE TabModules ADD CONSTRAINT IX_TabModules UNIQUE NONCLUSTERED (TabID, ModuleID)
        // [03.00.03:L11-16], never dropped outside UnInstall.SqlDataProvider.
        builder.HasIndex(t => new { t.TabId, t.ModuleId })
            .IsUnique()
            .HasDatabaseName("IX_TabModules");

        // Both foreign keys are declared ON DELETE CASCADE in the schema and both are reproduced faithfully
        // - FK_TabModules_Tabs on TabID referencing dbo.Tabs at 03.00.01:L44-52 and FK_TabModules_Modules
        // on ModuleID referencing dbo.Modules at 03.00.01:L56-64.
        builder.HasOne(t => t.Tab)
            .WithMany(tab => tab.TabModules)
            .HasForeignKey(t => t.TabId)
            .HasConstraintName("FK_TabModules_Tabs")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(t => t.Module)
            .WithMany(m => m.TabModules)
            .HasForeignKey(t => t.ModuleId)
            .HasConstraintName("FK_TabModules_Modules")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
