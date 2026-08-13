using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="UserRole"/> entity to the existing <c>dbo.UserRoles</c> table - the row that grants
/// one account one role, for a bounded stretch of time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Six columns, and the two that carry the assignment's meaning are dates rather than a state.</b> A
/// membership is pending, active or expired according to where the current instant falls between
/// <c>EffectiveDate</c> and <c>ExpiryDate</c>. No column records that classification, in this table or in
/// any other, so the mapping deliberately has nothing to say about it and the entity computes it on demand.
/// </para>
/// <para>
/// <b><c>IsTrialUsed</c> is a nullable flag, so three states are representable</b> - the trial was used,
/// the trial was not used, and nothing is recorded. The mapping preserves all three rather than collapsing
/// the third into the second, which is a real behavioural difference from the legacy code and is the reason
/// the column carries neither a requiredness nor a default here.
/// </para>
/// </remarks>
internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    /// <summary>Applies the mapping.</summary>
    /// <param name="builder">The builder for the role assignment entity type.</param>
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Website/release.config:L354 registers the SQL data provider with objectQualifier="" and:L355 with
        // databaseOwner="dbo", so the physical object is the unqualified UserRoles under the dbo schema.
        // Every physical name stated in this file follows from that pair.
        builder.ToTable("UserRoles", "dbo");

        // The primary key is the SURROGATE and it is clustered. 01.00.00:L441-446 reads ALTER TABLE
        // dbo.UserRoles WITH NOCHECK ADD CONSTRAINT PK_UserRoles PRIMARY KEY CLUSTERED ( UserRoleID ) ON
        // PRIMARY and no script drops it; the 02.00.00 sp_rename calls that mention this table pass the
        // same name unchanged in both arguments and so are no-ops that prove nothing either way.
        builder.HasKey(x => x.UserRoleId).HasName("PK_UserRoles");

        // UserRoleID int IDENTITY (1, 1) NOT NULL at 01.00.00:L239. The seed is 1, the ordinary case,
        // unlike dbo.Roles and dbo.Tabs which seed at 0 and dbo.Portals which seeds at -1.
        builder.Property(x => x.UserRoleId)
            .HasColumnName("UserRoleID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // Upper-case ID, and the casing is per table rather than per schema. This table declares UserID int
        // NOT NULL at 01.00.00:L240 and RoleID int NOT NULL at:L241, while dbo.UserPortals spells the very
        // same two ideas UserId and PortalId with a lower-case d.
        builder.Property(x => x.UserId)
            .HasColumnName("UserID")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(x => x.RoleId)
            .HasColumnName("RoleID")
            .HasColumnType("int")
            .IsRequired();

        // ExpiryDate datetime NULL at 01.00.00:L242, present since the baseline. The type is stated
        // explicitly because EF Core's default for DateTime? is the newer higher-precision type, which this
        // column is not.
        builder.Property(x => x.ExpiryDate)
            .HasColumnName("ExpiryDate")
            .HasColumnType("datetime");

        // This deliberately differs from dbo.Roles.IsPublic and dbo.Roles.AutoAssignment, which are bit NOT
        // NULL with a store default of 0 and are configured accordingly in RoleConfiguration.
        builder.Property(x => x.IsTrialUsed)
            .HasColumnName("IsTrialUsed")
            .HasColumnType("bit");

        // Legacy datetime and nullable, with the same Rule T7 treatment as ExpiryDate above and no sentinel
        // conversion: a null means the membership counts immediately, matching the inclusive effective-date
        // half of the terminal GetRolesByUser predicate.
        builder.Property(x => x.EffectiveDate)
            .HasColumnName("EffectiveDate")
            .HasColumnType("datetime");

        // The two index names are CROSSED relative to the columns they cover, and the pairing is easy to
        // reverse by accident. 01.00.10:L747-748 and:L755-756 created the pair, 03.00.09:L413 and:L415
        // dropped it, and:L417-418 recreated them in their terminal form: CREATE NONCLUSTERED INDEX
        // IX_UserRoles ON UserRoles (RoleID) CREATE NONCLUSTERED INDEX IX_UserRoles_1 ON UserRoles (UserID)
        // So the unsuffixed name covers RoleID and the _1 suffix covers UserID. dbo.UserPortals is reworked
        // in the same 03.00.09 block, three lines earlier, with its unsuffixed name over the PORTAL side -
        // so the two link tables are not symmetrical and the pairing must be read off the citations rather
        // than inferred from the neighbour.
        builder.HasIndex(x => x.RoleId).HasDatabaseName("IX_UserRoles");
        builder.HasIndex(x => x.UserId).HasDatabaseName("IX_UserRoles_1");

        builder.HasOne(x => x.Role)
            .WithMany(r => r.UserRoles)
            .HasForeignKey(x => x.RoleId)
            .HasConstraintName("FK_UserRoles_Roles")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.User)
            .WithMany(u => u.UserRoles)
            .HasForeignKey(x => x.UserId)
            .HasConstraintName("FK_UserRoles_Users")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
