using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="ModuleDefinition"/> entity to the legacy <c>dbo.ModuleDefinitions</c> table.
/// </summary>
/// <remarks>
/// <para>
/// The terminal table is far narrower than the baseline one. The 02.00.00 script dropped nine
/// columns in a single statement — <c>DesktopSrc</c>, <c>MobileSrc</c>, <c>EditSrc</c>,
/// <c>Secure</c>, <c>EditModuleIcon</c>, <c>AdminTabIcon</c>, <c>AdminOrder</c>,
/// <c>Description</c> and <c>IsPremium</c> — moving the control locations into
/// <c>ModuleControls</c> and the packaging metadata into <c>DesktopModules</c>. An earlier script
/// had already dropped <c>HostFee</c>. Four columns remain.
/// </para>
/// <para>
/// <c>FriendlyName</c> carries a unique non-clustered index named <c>IX_ModuleDefinitions</c> that
/// is still present at the terminal state, so a definition name is unique across the whole
/// installation and not merely within its package. The uniqueness matters to the module service,
/// which resolves the site-settings definition by name.
/// </para>
/// <para>
/// The identity seed is <c>1</c>, so no definition can ever carry an identifier below one. The
/// permission service relies on that fact to reject an impossible definition identifier without a
/// round trip.
/// </para>
/// </remarks>
internal sealed class ModuleDefinitionConfiguration : IEntityTypeConfiguration<ModuleDefinition>
{
    /// <summary>
    /// Applies the mapping.
    /// </summary>
    /// <param name="builder">The builder for the module definition entity type.</param>
    public void Configure(EntityTypeBuilder<ModuleDefinition> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ModuleDefinitions", "dbo");

        builder.HasKey(d => d.ModuleDefinitionId).HasName("PK_ModuleDefinitions");

        builder.Property(d => d.ModuleDefinitionId)
            .HasColumnName("ModuleDefID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        builder.Property(d => d.FriendlyName)
            .HasColumnName("FriendlyName")
            .HasMaxLength(128)
            .IsRequired();

        // The store default of zero that accompanied this column when it was introduced was dropped
        // in the same script, so the column is required with no default of its own.
        builder.Property(d => d.DesktopModuleId)
            .HasColumnName("DesktopModuleID")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(d => d.DefaultCacheTime)
            .HasColumnName("DefaultCacheTime")
            .HasColumnType("int")
            .IsRequired()
            .HasDefaultValue(0);

        builder.HasOne(d => d.DesktopModule)
            .WithMany(p => p.ModuleDefinitions)
            .HasForeignKey(d => d.DesktopModuleId)
            .HasConstraintName("FK_ModuleDefinitions_DesktopModules")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(d => d.FriendlyName)
            .IsUnique()
            .HasDatabaseName("IX_ModuleDefinitions");
    }
}
