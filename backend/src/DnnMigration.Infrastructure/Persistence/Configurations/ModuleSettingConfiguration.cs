using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>Binds the <see cref="ModuleSetting"/> entity to the existing <c>dbo.ModuleSettings</c> table.</summary>
/// <remarks>
/// <para>
/// This is a genuine name-and-value store, which is what separates it from the portal settings the legacy
/// code only appeared to have. It exists in the schema, the upgrade chain rebuilds and then re-keys it, and
/// it carries a composite clustered primary key over the module identifier and the setting name.
/// </para>
/// <para>
/// <b>The primary key is a real database constraint, and the chain hides it twice over.</b> The scripts are
/// carriage-return delimited and they interpolate the object qualifier into constraint names, so
/// <c>PK_{objectQualifier}ModuleSettings</c> is split across a line break at 02.00.01:L47-48 and a plain
/// search for the resolved name finds nothing at all - which invites the false conclusion that only a
/// non-unique index stands over the two columns and that the key declared here exists merely to satisfy the
/// modelling requirement.
/// </para>
/// </remarks>
internal sealed class ModuleSettingConfiguration : IEntityTypeConfiguration<ModuleSetting>
{
    /// <summary>Applies the mapping for one stored setting of a module instance.</summary>
    /// <param name="builder">The builder for the module setting entity type.</param>
    /// <exception cref="ArgumentNullException"><c>builder</c> is <see langword="null"/>.</exception>
    public void Configure(EntityTypeBuilder<ModuleSetting> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Website/release.config:L354-355 registers the data provider with objectQualifier="" and
        // databaseOwner="dbo", so the table resolves to its unqualified name under the dbo schema and every
        // constraint name below resolves to its own bare form.
        builder.ToTable("ModuleSettings", "dbo");

        // This composite key mirrors a constraint the database already carries.
        builder.HasKey(s => new { s.ModuleId, s.SettingName }).HasName("PK_ModuleSettings");

        // Dbo.Modules.ModuleID is declared IDENTITY (0, 1) at 01.00.00:L221, so the first module of an
        // installation is numbered zero and a zero in this column is a legitimate parent reference.
        builder.Property(s => s.ModuleId)
            .HasColumnName("ModuleID")
            .HasColumnType("int")
            .HasSentinel(int.MinValue)
            .ValueGeneratedNever()
            .IsRequired();

        // 01.00.08:L6255 - SettingName nvarchar(50) NOT NULL, the second column of the key above. The
        // column stores the name exactly as written, so the case a caller supplied is what is read back.
        builder.Property(s => s.SettingName)
            .HasColumnName("SettingName")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(s => s.SettingValue)
            .HasColumnName("SettingValue")
            .HasMaxLength(2000)
            .IsRequired();

        // 01.00.08:L6277-6285 - ALTER TABLE dbo.ModuleSettings WITH NOCHECK ADD CONSTRAINT
        // FK_ModuleSettings_Modules FOREIGN KEY (ModuleID) REFERENCES dbo.Modules (ModuleID) ON DELETE
        // CASCADE NOT FOR REPLICATION. That is the terminal form: the constraint was first added at
        // 01.00.00:L790-796, dropped at 01.00.08:L6248-6249 so the table could be rebuilt around a widened
        // value column, and re-added in the shape above; the only later mention is a rename to its own name
        // at 02.00.00:L145.
        builder.HasOne(s => s.Module)
            .WithMany(m => m.Settings)
            .HasForeignKey(s => s.ModuleId)
            .HasConstraintName("FK_ModuleSettings_Modules")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
