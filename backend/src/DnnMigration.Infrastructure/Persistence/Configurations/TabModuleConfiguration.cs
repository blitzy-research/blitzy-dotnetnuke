using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="TabModule"/> entity to the legacy <c>dbo.TabModules</c> table.
/// </summary>
/// <remarks>
/// <para>
/// This table is the placement of a module on a page, and it exists because the 03.00.01 script
/// removed the placement columns from <c>Modules</c> and re-homed them here. Everything about how a
/// module appears — its pane, its order within that pane, its cache window, its alignment, colour,
/// border, icon, visibility state and container — is a property of the placement and not of the
/// module, which is why the legacy flattened view of the two was so misleading.
/// </para>
/// <para>
/// The unique index <c>IX_TabModules</c> over the page and module pair means a module can appear at
/// most once on any given page, while still appearing on many pages.
/// </para>
/// <para>
/// Three display flags carry a store default of <c>1</c>, added by <c>03.00.08</c> and re-asserted by
/// <c>03.01.01</c>. As in the tab mapping, those defaults are deliberately not configured here,
/// because the CLR default of <see langword="false"/> differs from them and configuring them would
/// let a request to hide a title, a print affordance or a syndication affordance be silently
/// reversed. Every write therefore sends an explicit value and the store default only ever applies to
/// a writer outside this model.
/// </para>
/// <para>
/// The entity's own initialisers match those store defaults for <c>DisplayTitle</c> and
/// <c>DisplayPrint</c> but deliberately <b>not</b> for <c>DisplaySyndicate</c>, which initialises to
/// <see langword="false"/>. That is not an oversight to be corrected in either direction: the legacy
/// constructor and initialiser in <c>Library/Components/Modules/ModuleInfo.vb</c> both set it to
/// <c>False</c> while the column default is <c>1</c>, so object construction and the store genuinely
/// disagreed in the legacy system. Both behaviours are preserved on the side that owns them - the
/// entity keeps the constructor's answer, this mapping leaves the column default untouched - and the
/// divergence is recorded in the repository-root <c>MIGRATION_NOTES.md</c>.
/// </para>
/// <para>
/// <c>Border</c> is <c>nvarchar(1)</c> — a single character used as a flag by the legacy renderer.
/// Its length is declared rather than rounded up, so an over-long value fails here instead of being
/// truncated by the provider.
/// </para>
/// </remarks>
internal sealed class TabModuleConfiguration : IEntityTypeConfiguration<TabModule>
{
    /// <summary>
    /// Applies the mapping.
    /// </summary>
    /// <param name="builder">The builder for the placement entity type.</param>
    public void Configure(EntityTypeBuilder<TabModule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TabModules", "dbo");

        builder.HasKey(p => p.TabModuleId).HasName("PK_TabModules");

        builder.Property(p => p.TabModuleId)
            .HasColumnName("TabModuleID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        builder.Property(p => p.TabId)
            .HasColumnName("TabID")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(p => p.ModuleId)
            .HasColumnName("ModuleID")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(p => p.PaneName)
            .HasColumnName("PaneName")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(p => p.ModuleOrder)
            .HasColumnName("ModuleOrder")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(p => p.CacheTime)
            .HasColumnName("CacheTime")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(p => p.Alignment)
            .HasColumnName("Alignment")
            .HasMaxLength(10);

        builder.Property(p => p.Color)
            .HasColumnName("Color")
            .HasMaxLength(20);

        builder.Property(p => p.Border)
            .HasColumnName("Border")
            .HasMaxLength(1);

        builder.Property(p => p.IconFile)
            .HasColumnName("IconFile")
            .HasMaxLength(100);

        builder.Property(p => p.Visibility)
            .HasColumnName("Visibility")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(p => p.ContainerSrc)
            .HasColumnName("ContainerSrc")
            .HasMaxLength(200);

        builder.Property(p => p.DisplayTitle)
            .HasColumnName("DisplayTitle")
            .HasColumnType("bit")
            .IsRequired();

        builder.Property(p => p.DisplayPrint)
            .HasColumnName("DisplayPrint")
            .HasColumnType("bit")
            .IsRequired();

        builder.Property(p => p.DisplaySyndicate)
            .HasColumnName("DisplaySyndicate")
            .HasColumnType("bit")
            .IsRequired();

        builder.HasOne(p => p.Tab)
            .WithMany(t => t.TabModules)
            .HasForeignKey(p => p.TabId)
            .HasConstraintName("FK_TabModules_Tabs")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(p => p.Module)
            .WithMany(m => m.TabModules)
            .HasForeignKey(p => p.ModuleId)
            .HasConstraintName("FK_TabModules_Modules")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(p => new { p.TabId, p.ModuleId })
            .IsUnique()
            .HasDatabaseName("IX_TabModules");
    }
}
