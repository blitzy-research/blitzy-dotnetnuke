using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="ModuleControl"/> entity to the legacy <c>dbo.ModuleControls</c> table.
/// </summary>
/// <remarks>
/// <para>
/// The table records the user-interface entry points a module definition offers. The presentation
/// layer those entry points addressed is out of scope, but the rows themselves are not: the module
/// administration screens list the available control keys, so the entity is mapped and read.
/// </para>
/// <para>
/// <c>ModuleDefID</c> is nullable here even though the cascading foreign key to
/// <c>ModuleDefinitions</c> exists, which is how the schema models a control that belongs to no
/// particular definition. The relationship is therefore optional and still cascades, exactly as
/// declared.
/// </para>
/// <para>
/// The unique index <c>IX_ModuleControls</c> spans <c>ModuleDefID</c>, <c>ControlKey</c> and
/// <c>ControlSrc</c> together, so one definition may expose the same key from different sources and
/// the same source under different keys, but not the same pair twice.
/// </para>
/// </remarks>
internal sealed class ModuleControlConfiguration : IEntityTypeConfiguration<ModuleControl>
{
    /// <summary>
    /// Applies the mapping.
    /// </summary>
    /// <param name="builder">The builder for the module control entity type.</param>
    public void Configure(EntityTypeBuilder<ModuleControl> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ModuleControls", "dbo");

        builder.HasKey(c => c.ModuleControlId).HasName("PK_ModuleControls");

        builder.Property(c => c.ModuleControlId)
            .HasColumnName("ModuleControlID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        builder.Property(c => c.ModuleDefinitionId)
            .HasColumnName("ModuleDefID")
            .HasColumnType("int");

        builder.Property(c => c.ControlKey)
            .HasColumnName("ControlKey")
            .HasMaxLength(20);

        builder.Property(c => c.ControlTitle)
            .HasColumnName("ControlTitle")
            .HasMaxLength(50);

        builder.Property(c => c.ControlSrc)
            .HasColumnName("ControlSrc")
            .HasMaxLength(256);

        builder.Property(c => c.IconFile)
            .HasColumnName("IconFile")
            .HasMaxLength(100);

        builder.Property(c => c.ControlType)
            .HasColumnName("ControlType")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(c => c.ViewOrder)
            .HasColumnName("ViewOrder")
            .HasColumnType("int");

        builder.Property(c => c.HelpUrl)
            .HasColumnName("HelpUrl")
            .HasMaxLength(200);

        builder.Property(c => c.SupportsPartialRendering)
            .HasColumnName("SupportsPartialRendering")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        builder.HasOne(c => c.ModuleDefinition)
            .WithMany(d => d.ModuleControls)
            .HasForeignKey(c => c.ModuleDefinitionId)
            .HasConstraintName("FK_ModuleControls_ModuleDefinitions")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(c => new { c.ModuleDefinitionId, c.ControlKey, c.ControlSrc })
            .IsUnique()
            .HasDatabaseName("IX_ModuleControls");
    }
}
