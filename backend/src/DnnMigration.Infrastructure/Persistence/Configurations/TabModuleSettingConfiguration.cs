using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="TabModuleSetting"/> entity to the legacy <c>dbo.TabModuleSettings</c> table.
/// </summary>
/// <remarks>
/// <para>
/// These are the settings of a placement rather than of a module, so the same module on two pages
/// can be configured differently. The composite clustered primary key over the placement identifier
/// and the setting name is declared as the schema has it.
/// </para>
/// <para>
/// The value column is <c>nvarchar(2000)</c>, nearly eight times the width its module-level
/// counterpart allows. The difference is real and load bearing, which is why the module service
/// validates the two lengths independently rather than applying one limit to both.
/// </para>
/// </remarks>
internal sealed class TabModuleSettingConfiguration : IEntityTypeConfiguration<TabModuleSetting>
{
    /// <summary>
    /// Applies the mapping.
    /// </summary>
    /// <param name="builder">The builder for the placement setting entity type.</param>
    public void Configure(EntityTypeBuilder<TabModuleSetting> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TabModuleSettings", "dbo");

        builder.HasKey(s => new { s.TabModuleId, s.SettingName }).HasName("PK_TabModuleSettings");

        builder.Property(s => s.TabModuleId)
            .HasColumnName("TabModuleID")
            .HasColumnType("int")
            .ValueGeneratedNever();

        builder.Property(s => s.SettingName)
            .HasColumnName("SettingName")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(s => s.SettingValue)
            .HasColumnName("SettingValue")
            .HasMaxLength(2000)
            .IsRequired();

        builder.HasOne(s => s.TabModule)
            .WithMany(p => p.TabModuleSettings)
            .HasForeignKey(s => s.TabModuleId)
            .HasConstraintName("FK_TabModuleSettings_TabModules")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
