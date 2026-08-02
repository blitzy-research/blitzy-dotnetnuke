using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using DnnMigration.Infrastructure.Persistence.ValueConverters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="Role"/> aggregate to the legacy <c>dbo.Roles</c> table.
/// </summary>
/// <remarks>
/// <para>
/// The identity seed is <c>0</c>, so the first role in a fresh installation carries the identifier
/// zero. Zero is a genuine role and must never be read as "no role".
/// </para>
/// <para>
/// <c>PortalID</c> is nullable: a role with no tenant is a host-level role. The unique index
/// <c>IX_RoleName</c> spans the tenant and the role name together, so the same name may be used once
/// per tenant.
/// </para>
/// <para>
/// The two frequency columns are <c>char(1)</c> holding the single-letter codes the legacy billing
/// logic switched on. Those letters are stored data, not an implementation detail, so the enumeration
/// they map to carries the letters as its own values and a converter moves between the two. Renaming
/// or renumbering the enumeration would silently invalidate every existing row, which is why the
/// values are pinned to the characters.
/// </para>
/// <para>
/// The converter tolerates a blank stored value by reading it as the absent code rather than
/// throwing. The column is nullable, so a genuine database null still surfaces as
/// <see langword="null"/>; the tolerance covers the empty string that the legacy sentinel system
/// used interchangeably with null for text.
/// </para>
/// <para>
/// Both monetary columns are <c>money</c> in the terminal schema, and each is declared with its own
/// store type rather than inheriting one by convention. The store default of zero on the service fee is not
/// configured, because the property is nullable and an absent fee must remain absent rather than
/// becoming a charge of nothing.
/// </para>
/// <para>
/// The baseline foreign key from the billing frequency to a code table was dropped at 03.00.01 and
/// never restored, so no relationship is declared for it. The role-group reference has a foreign key
/// with no <c>ON DELETE</c> clause and is therefore mapped with no delete behaviour.
/// </para>
/// </remarks>
internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    // MIGRATION: the enumeration-to-char(1) mapping is NOT declared here. It lives once, on
    // Persistence/ValueConverters/BillingFrequencyToStringConverter.cs, and both frequency columns
    // read it from there. A second, local mapping would be a second authority on what a stored code
    // means: this one additionally preserves the difference between an absent frequency (NULL, which
    // this schema permits) and the declared "None" member, and refuses a character the enumeration
    // does not declare rather than casting it through.

    /// <summary>
    /// Applies the mapping.
    /// </summary>
    /// <param name="builder">The builder for the role entity type.</param>
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Roles", "dbo");

        builder.HasKey(r => r.RoleId).HasName("PK_Roles");

        builder.Property(r => r.RoleId)
            .HasColumnName("RoleID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(0, 1);

        builder.Property(r => r.PortalId)
            .HasColumnName("PortalID")
            .HasColumnType("int");

        builder.Property(r => r.RoleName)
            .HasColumnName("RoleName")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(r => r.Description)
            .HasColumnName("Description")
            .HasMaxLength(1000);

        // 01.00.04:L1326 rebuilds the table with "ServiceFee money NULL" and L1341 copies the old
        // column across with CONVERT(money, ServiceFee); 01.00.05:L2752 re-declares it as money in a
        // second rebuild and 03.01.01:L1173 re-asserts that type. The 01.00.00:L119 decimal(5,2)
        // declaration is therefore superseded, and money is the terminal store type.
        builder.Property(r => r.ServiceFee)
            .HasColumnName("ServiceFee")
            .HasColumnType("money");

        builder.Property(r => r.BillingFrequency)
            .HasColumnName("BillingFrequency")
            .HasMaxLength(1)
            .IsFixedLength()
            .IsUnicode(false)
            .HasConversion(BillingFrequencyToStringConverter.Instance);

        builder.Property(r => r.TrialPeriod)
            .HasColumnName("TrialPeriod")
            .HasColumnType("int");

        builder.Property(r => r.TrialFrequency)
            .HasColumnName("TrialFrequency")
            .HasMaxLength(1)
            .IsFixedLength()
            .IsUnicode(false)
            .HasConversion(BillingFrequencyToStringConverter.Instance);

        builder.Property(r => r.BillingPeriod)
            .HasColumnName("BillingPeriod")
            .HasColumnType("int");

        builder.Property(r => r.TrialFee)
            .HasColumnName("TrialFee")
            .HasColumnType("money");

        builder.Property(r => r.IsPublic)
            .HasColumnName("IsPublic")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(r => r.AutoAssignment)
            .HasColumnName("AutoAssignment")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(r => r.RoleGroupId)
            .HasColumnName("RoleGroupID")
            .HasColumnType("int");

        builder.Property(r => r.RsvpCode)
            .HasColumnName("RSVPCode")
            .HasMaxLength(50);

        builder.Property(r => r.IconFile)
            .HasColumnName("IconFile")
            .HasMaxLength(100);

        builder.HasOne(r => r.Portal)
            .WithMany(p => p.Roles)
            .HasForeignKey(r => r.PortalId)
            .HasConstraintName("FK_Roles_Portals")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.RoleGroup)
            .WithMany(g => g.Roles)
            .HasForeignKey(r => r.RoleGroupId)
            .HasConstraintName("FK_Roles_RoleGroups")
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(r => new { r.PortalId, r.RoleName })
            .IsUnique()
            .HasDatabaseName("IX_RoleName");
    }
}
