using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>Binds the <see cref="UserProfileValue"/> entity to the legacy <c>dbo.UserProfile</c> table.</summary>
/// <remarks>
/// <para>
/// The table is named <c>UserProfile</c> - singular, and neither a pluralisation nor a suffixing of the
/// entity name - so naming it is the first thing this configuration does. Convention would derive a plural
/// name that no object in this schema answers to, and nothing in the build would notice: the model would
/// compile, validate, and then fail at run time against a table that does not exist.
/// </para>
/// <para>
/// The table gives each value two possible homes. The bounded column holds 3750 characters and the overflow
/// column is a long-text type, and the legacy write procedure fills exactly one of the two per row on a
/// length test, nulling the other.
/// </para>
/// </remarks>
internal sealed class UserProfileValueConfiguration : IEntityTypeConfiguration<UserProfileValue>
{
    /// <summary>Applies the mapping.</summary>
    /// <param name="builder">The builder for the profile value entity type.</param>
    public void Configure(EntityTypeBuilder<UserProfileValue> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The legacy table is the SINGULAR "UserProfile" - CREATE TABLE UserProfile at 04.00.04:L1411 -
        // while the destination entity is UserProfileValue, named to separate the per-user VALUE row from
        // the definition row that describes it.
        builder.ToTable("UserProfile", "dbo");

        // The primary key is declared PRIMARY KEY NONCLUSTERED (04.00.04:L1422-1423 = 03.02.03:L1375-1376),
        // and no later script drops it.
        builder.HasKey(v => v.ProfileId).HasName("PK_UserProfile").IsClustered(false);

        // 04.00.04:L1413 (= 03.02.03:L1366) - ProfileID int IDENTITY(1,1) NOT NULL. Note the upper-case ID
        // suffix. The seed is recorded so a generated script would continue the existing sequence rather
        // than restart it.
        builder.Property(v => v.ProfileId)
            .HasColumnName("ProfileID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // 04.00.04:L1414 (= 03.02.03:L1367) - UserID int NOT NULL.
        builder.Property(v => v.UserId)
            .HasColumnName("UserID")
            .HasColumnType("int")
            .IsRequired();

        // 04.00.04:L1415 (= 03.02.03:L1368) - PropertyDefinitionID int NOT NULL. Upper-case ID suffix
        // again, matching the principal column of the same name on dbo.ProfilePropertyDefinition.
        builder.Property(v => v.PropertyDefinitionId)
            .HasColumnName("PropertyDefinitionID")
            .HasColumnType("int")
            .IsRequired();

        // 04.00.04:L1416 (= 03.02.03:L1369) - PropertyValue nvarchar(3750) NULL. The exact bound is carried
        // rather than rounded.
        builder.Property(v => v.PropertyValue)
            .HasColumnName("PropertyValue")
            .HasMaxLength(3750);

        // PropertyText is a REAL terminal column and must never be dropped (04.00.04:L1417 =
        // 03.02.03:L1370, declared as a nullable long-text column).
        builder.Property(v => v.PropertyText)
            .HasColumnName("PropertyText")
            .HasColumnType("ntext");

        // Visibility is a PROFILE-visibility integer with no enumeration in the domain, and it is
        // deliberately NOT the module-visibility enumeration.
        builder.Property(v => v.Visibility)
            .HasColumnName("Visibility")
            .HasColumnType("int")
            .IsRequired()
            .HasDefaultValue(0);

        // LastUpdatedDate is the legacy datetime type (04.00.04:L1419 = 03.02.03:L1372, LastUpdatedDate
        // datetime NOT NULL).
        builder.Property(v => v.LastUpdatedDate)
            .HasColumnName("LastUpdatedDate")
            .HasColumnType("datetime")
            .IsRequired();

        // 03.03.02:L137-138, re-issued identically at 04.03.02:L138-139 IF NOT EXISTS ( SELECT * FROM
        // sysindexes WHERE name = N'IX_UserProfile') CREATE NONCLUSTERED INDEX IX_UserProfile ON
        // UserProfile (UserID) ON PRIMARY Existence-guarded in both scripts, so the pair is ONE logical
        // change, and never dropped.
        builder.HasIndex(v => v.UserId).HasDatabaseName("IX_UserProfile");

        // Both terminal foreign keys declare ON DELETE CASCADE explicitly and both are reproduced
        // faithfully, at 04.00.04:L1425-1429 (= 03.02.03:L1378-1382): ALTER TABLE UserProfile WITH NOCHECK
        // ADD CONSTRAINT FK_UserProfile_Users FOREIGN KEY(UserID) REFERENCES Users (UserID) ON DELETE
        // CASCADE ALTER TABLE UserProfile WITH NOCHECK ADD CONSTRAINT
        // FK_UserProfile_ProfilePropertyDefinition FOREIGN KEY(PropertyDefinitionID) REFERENCES
        // ProfilePropertyDefinition (PropertyDefinitionID) ON DELETE CASCADE.
        builder.HasOne(v => v.User)
            .WithMany(u => u.UserProfileValues)
            .HasForeignKey(v => v.UserId)
            .HasConstraintName("FK_UserProfile_Users")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(v => v.PropertyDefinition)
            .WithMany(d => d.ProfileValues)
            .HasForeignKey(v => v.PropertyDefinitionId)
            .HasConstraintName("FK_UserProfile_ProfilePropertyDefinition")
            .OnDelete(DeleteBehavior.Cascade);

        // Nothing else is mapped and nothing is ignored.
    }
}
