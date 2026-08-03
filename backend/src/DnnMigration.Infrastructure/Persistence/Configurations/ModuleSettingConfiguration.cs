using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="ModuleSetting"/> entity to the legacy <c>dbo.ModuleSettings</c> table.
/// </summary>
/// <remarks>
/// <para>
/// This is a genuine key and value table, unlike the portal settings the legacy code appeared to
/// have: it exists in the schema, the upgrade chain alters it thirteen times, and it carries a
/// composite clustered primary key over the module identifier and the setting name. The legacy code
/// surfaced these rows as an untyped hash table; the entity models the row itself, so the composite
/// key is declared rather than a surrogate being invented.
/// </para>
/// <para>
/// The setting value is <c>nvarchar(2000)</c>, the same width its per-placement counterpart allows.
/// The two tables are structurally identical in this respect. An earlier revision of this file bound
/// the column at 256, which is the width the baseline script declared and not the width the schema
/// ends up with: <c>01.00.08.SqlDataProvider</c> lines 6248-6286 rebuild the whole table through a
/// <c>Tmp_ModuleSettings</c> copy that widens the column to <c>nvarchar(2000) NOT NULL</c> at line
/// 6256, and nothing later narrows it. Binding 256 refused values the legacy application accepts, so
/// the correction is recorded here rather than silently absorbed.
/// </para>
/// </remarks>
internal sealed class ModuleSettingConfiguration : IEntityTypeConfiguration<ModuleSetting>
{
    /// <summary>
    /// Applies the mapping.
    /// </summary>
    /// <param name="builder">The builder for the module setting entity type.</param>
    public void Configure(EntityTypeBuilder<ModuleSetting> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ModuleSettings", "dbo");

        builder.HasKey(s => new { s.ModuleId, s.SettingName }).HasName("PK_ModuleSettings");

        // The sentinel is moved off zero deliberately, and it is not cosmetic. This property is part of the
        // primary key AND part of the foreign key to Modules, and for such a property the provider treats a
        // value equal to the CLR default as "not supplied yet, the principal row must still be inserted".
        // Modules.ModuleID is IDENTITY(0, 1), so the first module of an installation legitimately carries
        // the identifier zero, and inserting any setting against it would otherwise be refused with a
        // complaint that the module identifier is unknown. Declaring an out-of-range sentinel restores the
        // schema's own meaning: zero identifies a module, it does not mean "absent".
        builder.Property(s => s.ModuleId)
            .HasColumnName("ModuleID")
            .HasColumnType("int")
            .HasSentinel(int.MinValue)
            .ValueGeneratedNever();

        builder.Property(s => s.SettingName)
            .HasColumnName("SettingName")
            .HasMaxLength(50)
            .IsRequired();

        // MIGRATION: the width is the terminal 2000, not the baseline 256. 01.00.00:L353 created the
        // column nvarchar(256) NOT NULL, but 01.00.08:L6248-6286 destroys and rebuilds the table with
        // SettingValue nvarchar(2000) NOT NULL (line 6256) and no later script narrows it. The terminal
        // writers corroborate: @SettingValue nvarchar(2000) on UpdateModuleSetting at 01.00.08:L6295
        // and on AddModuleSetting/UpdateModuleSetting at 02.00.00:L4147 and :L4171. The column is
        // NOT NULL, and the legacy Null.NullString sentinel is the empty string, so "no value" is
        // stored as '' rather than as SQL NULL - hence non-nullable with no sentinel translation here.
        builder.Property(s => s.SettingValue)
            .HasColumnName("SettingValue")
            .HasMaxLength(2000)
            .IsRequired();

        builder.HasOne(s => s.Module)
            .WithMany(m => m.Settings)
            .HasForeignKey(s => s.ModuleId)
            .HasConstraintName("FK_ModuleSettings_Modules")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
