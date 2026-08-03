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
/// The setting value is <c>nvarchar(256)</c> here, which is markedly shorter than the
/// <c>nvarchar(2000)</c> its per-placement counterpart allows. The module service enforces the two
/// different lengths separately for exactly this reason.
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

        builder.Property(s => s.SettingValue)
            .HasColumnName("SettingValue")
            .HasMaxLength(256)
            .IsRequired();

        builder.HasOne(s => s.Module)
            .WithMany(m => m.Settings)
            .HasForeignKey(s => s.ModuleId)
            .HasConstraintName("FK_ModuleSettings_Modules")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
