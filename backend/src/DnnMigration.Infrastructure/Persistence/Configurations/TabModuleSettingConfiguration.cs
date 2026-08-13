using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="TabModuleSetting"/> entity to the existing <c>dbo.TabModuleSettings</c> table.
/// </summary>
/// <remarks>
/// <para>
/// The schema is externally owned and immutable for this migration. Every declaration below describes the
/// terminal state of the upgrade chain under <c>Website/Providers/DataProviders/SqlDataProvider/</c> as it
/// already stands, and nothing in this assembly creates, alters or removes a database object.
/// </para>
/// </remarks>
internal sealed class TabModuleSettingConfiguration : IEntityTypeConfiguration<TabModuleSetting>
{
    /// <summary>Applies the mapping for one stored setting of a single module placement.</summary>
    /// <param name="builder">The builder for the placement setting entity type.</param>
    /// <exception cref="ArgumentNullException"><c>builder</c> is <see langword="null"/>.</exception>
    public void Configure(EntityTypeBuilder<TabModuleSetting> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TabModuleSettings", "dbo");

        // This composite key mirrors a constraint the database already carries, and that constraint was
        // rebuilt rather than abandoned. 03.00.01:L730-735 declares PK_TabModuleSettings PRIMARY KEY
        // CLUSTERED (TabModuleID, SettingName), with the name on line 731; 03.00.09:L331 drops it; and
        // 03.00.09:L333 immediately re-adds it as PK_{objectQualifier}TabModuleSettings PRIMARY KEY
        // CLUSTERED ([TabModuleID], [SettingName]), which qualifies the constraint name and leaves the key
        // shape untouched.
        builder.HasKey(s => new { s.TabModuleId, s.SettingName }).HasName("PK_TabModuleSettings");

        builder.Property(s => s.TabModuleId)
            .HasColumnName("TabModuleID")
            .HasColumnType("int")
            .IsRequired();

        // nvarchar(50) NOT NULL [03.00.01:L724] - the second column of the key above. The column stores the
        // name exactly as written and so preserves case, which makes two names differing only in case two
        // distinct rows.
        builder.Property(s => s.SettingName)
            .HasColumnName("SettingName")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(s => s.SettingValue)
            .HasColumnName("SettingValue")
            .HasMaxLength(2000)
            .IsRequired();

        // 03.00.09:L335-336 - ALTER TABLE TabModuleSettings WITH NOCHECK ADD CONSTRAINT
        // FK_{objectQualifier}TabModuleSettings_{objectQualifier}TabModules FOREIGN KEY ([TabModuleID])
        // REFERENCES dbo.TabModules ([TabModuleID]) ON DELETE CASCADE NOT FOR REPLICATION. That is the
        // terminal form: the constraint was first added in exactly the same shape at 03.00.01:L746-754,
        // dropped at 03.00.09:L328-329, and re-added as above with only its name qualified, so the empty
        // object qualifier resolves it to FK_TabModuleSettings_TabModules.
        builder.HasOne(s => s.TabModule)
            .WithMany(t => t.Settings)
            .HasForeignKey(s => s.TabModuleId)
            .HasConstraintName("FK_TabModuleSettings_TabModules")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
