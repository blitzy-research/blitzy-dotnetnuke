using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence.ValueConverters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="Role"/> entity to the existing, immutable DotNetNuke <c>dbo.Roles</c> table.
/// </summary>
/// <remarks>
/// <para>
/// The schema is an input to this migration, never an output. Every declaration below reproduces the
/// <em>cumulative terminal</em> state of the eighty-eight upgrade scripts under
/// <c>Website/Providers/DataProviders/SqlDataProvider/</c>, and each carries the script and line that
/// settled it.
/// </para>
/// <para>
/// <c>Website/release.config</c> registers the data provider with <c>objectQualifier</c> empty (line 354)
/// and <c>databaseOwner</c> set to <c>dbo</c> (line 355), so the table is addressed unqualified in the
/// <c>dbo</c> schema.
/// </para>
/// </remarks>
internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    /// <summary>Applies the table, key, column, index and relationship mapping for the role entity type.</summary>
    /// <param name="builder">The builder used to configure the entity type.</param>
    /// <exception cref="ArgumentNullException"><c>builder</c> is <see langword="null"/>.</exception>
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Roles", "dbo");

        // PK_Roles is declared NONCLUSTERED [01.00.05:L2783-2788]. The earlier, textually identical keys at
        // 01.00.00:L490 and 01.00.04:L1357-1358 were destroyed by the two table rebuilds, and the no-op
        // rename at 02.00.00:L120 changes nothing; the 01.00.05 declaration is the terminal one.
        builder.HasKey(x => x.RoleId).HasName("PK_Roles").IsClustered(false);

        // The identity is seeded at ZERO - "RoleID int NOT NULL IDENTITY (0, 1)" [01.00.05:L2748],
        // restating the baseline at 01.00.00:L115 and the first rebuild at 01.00.04:L1322.
        builder.Property(x => x.RoleId)
            .HasColumnName("RoleID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(0, 1);

        builder.Property(x => x.PortalId)
            .HasColumnName("PortalID")
            .HasColumnType("int");

        // MIGRATION: nvarchar(50) NOT NULL [01.00.05:L2750]. Unique per portal, not globally - see
        // the composite index below.
        builder.Property(x => x.RoleName)
            .HasColumnName("RoleName")
            .HasMaxLength(50)
            .IsRequired();

        // MIGRATION: nvarchar(1000) NULL [01.00.05:L2751]. The width matches the sibling role-group
        // description and is deliberately wider than the 500 used by portals and tabs.
        builder.Property(x => x.Description)
            .HasColumnName("Description")
            .HasMaxLength(1000);

        // This column is NULL-able and yet carries a store default of 0 [DF_Roles_ServiceFee,
        // 03.01.01:L1177], re-added after the repair block above it drops the previous constraint by
        // dynamic name discovery [03.01.01:L1144] and so never names it in the script text.
        builder.Property(x => x.ServiceFee)
            .HasColumnName("ServiceFee")
            .HasColumnType("money")
            .HasDefaultValue(0m);

        builder.Property(x => x.BillingFrequency)
            .HasColumnName("BillingFrequency")
            .HasConversion(BillingFrequencyToStringConverter.Instance)
            .HasColumnType("char(1)")
            .HasMaxLength(1)
            .IsUnicode(false);

        // MIGRATION: int NULL [01.00.05:L2754], a count of trial-frequency units.
        builder.Property(x => x.TrialPeriod)
            .HasColumnName("TrialPeriod")
            .HasColumnType("int");

        builder.Property(x => x.TrialFrequency)
            .HasColumnName("TrialFrequency")
            .HasConversion(BillingFrequencyToStringConverter.Instance)
            .HasColumnType("char(1)")
            .HasMaxLength(1)
            .IsUnicode(false);

        builder.Property(x => x.BillingPeriod)
            .HasColumnName("BillingPeriod")
            .HasColumnType("int");

        builder.Property(x => x.TrialFee)
            .HasColumnName("TrialFee")
            .HasColumnType("money");

        // Added as "bit NOT NULL" with a default of 0 [01.00.08:L6831], re-tightened at 03.01.01:L1174 and
        // its default re-added at 03.01.01:L1179 after the dynamic-name drop block [03.01.01:L1154].
        builder.Property(x => x.IsPublic)
            .HasColumnName("IsPublic")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        // Added as "bit NOT NULL" with a default of 0 [01.00.08:L6832], re-tightened at 03.01.01:L1175 and
        // its default re-added at 03.01.01:L1181 after the dynamic-name drop block [03.01.01:L1164].
        builder.Property(x => x.AutoAssignment)
            .HasColumnName("AutoAssignment")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(x => x.RoleGroupId)
            .HasColumnName("RoleGroupID")
            .HasColumnType("int");

        // The column name is fully upper-cased in the schema - "RSVPCode nvarchar(50) NULL"
        // [04.00.04:L79-80, equivalently 03.02.03:L44-45] - while the property follows C# casing, so the
        // mapping cannot be left to convention.
        builder.Property(x => x.RsvpCode)
            .HasColumnName("RSVPCode")
            .HasMaxLength(50);

        // MIGRATION: nvarchar(100) NULL [04.00.04:L79-80]. The value is a file reference that is
        // resolved above this layer; persistence stores it verbatim.
        builder.Property(x => x.IconFile)
            .HasColumnName("IconFile")
            .HasMaxLength(100);

        // THE FILTER IS EXPLICITLY SUPPRESSED. PortalID is nullable on this table - that is what lets an
        // installation-wide role exist with no owning tenant - and the SQL Server provider attaches a
        // "PortalID IS NOT NULL" predicate to a unique index over a nullable column unless told otherwise.
        builder.HasIndex(x => new { x.PortalId, x.RoleName })
            .IsUnique()
            .HasFilter(null)
            .HasDatabaseName("IX_RoleName");

        // MIGRATION: the terminal IX_Roles covers the BILLING FREQUENCY [03.00.09:L302], having been
        // dropped at 03.00.09:L300 and recreated there over that column.
        builder.HasIndex(x => x.BillingFrequency)
            .HasDatabaseName("IX_Roles");

        builder.HasOne(x => x.Portal)
            .WithMany(p => p.Roles)
            .HasForeignKey(x => x.PortalId)
            .HasConstraintName("FK_Roles_Portals")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.RoleGroup)
            .WithMany(g => g.Roles)
            .HasForeignKey(x => x.RoleGroupId)
            .HasConstraintName("FK_Roles_RoleGroups")
            .OnDelete(DeleteBehavior.NoAction);

        // No further association is declared here, and no principal-side collection is declared at all. A
        // role is the principal of the user-assignment table and of the two permission tables, and each of
        // those three is configured by its own dependent so the ownership rule above holds uniformly.
    }
}
