using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

// Everything configured below is the CUMULATIVE TERMINAL state of the DotNetNuke 4.9 upgrade chain, and
// never the shape of its baseline create script.

/// <summary>Binds <see cref="User"/> to the existing, unaltered DotNetNuke <c>dbo.Users</c> table.</summary>
/// <remarks>
/// <para>
/// Discovery and visibility. This type is <see langword="internal"/> because nothing consumes it directly:
/// the persistence context applies every configuration in this assembly by reflection, activating each one
/// through a parameterless constructor resolved with public-instance binding.
/// </para>
/// <para>
/// Legacy naming is honoured exactly. The legacy provider is registered with an empty object qualifier and
/// <c>dbo</c> as the database owner, so the table is addressed unqualified in the <c>dbo</c> schema and
/// every one of the nine columns carries an explicit name rather than relying on a naming convention.
/// </para>
/// </remarks>
internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    /// <summary>Applies the <c>dbo.Users</c> mapping to the supplied entity-type builder.</summary>
    /// <param name="builder">
    /// The builder for the <see cref="User"/> entity type, supplied by the model-building pipeline.
    /// </param>
    public void Configure(EntityTypeBuilder<User> builder)
    {
        // The table is addressed unqualified in the dbo schema: Website/release.config:L354 registers the
        // legacy provider with an empty object qualifier and:L355 registers dbo as the database owner, so
        // no prefix participates in the physical name.
        builder.ToTable("Users", "dbo");

        // The primary key is CLUSTERED in the terminal schema, not nonclustered as every earlier version of
        // it was.
        builder.HasKey(u => u.UserId).HasName("PK_Users");

        // UserID int NOT NULL IDENTITY(1, 1): 01.00.00:L98, carried unchanged through both rebuilds at
        // 01.00.05:L16 and 01.00.06:L184.
        builder.Property(u => u.UserId)
            .HasColumnName("UserID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // Username nvarchar(100) NOT NULL: introduced by the second table rebuild at 01.00.06:L197 and
        // back-filled from the then-existing email column immediately afterwards at 01.00.06:L268-269,
        // which is why long-lived installations hold login names that look like email addresses.
        builder.Property(u => u.Username)
            .HasColumnName("Username")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(u => u.FirstName)
            .HasColumnName("FirstName")
            .HasMaxLength(50)
            .IsRequired();

        // LastName is NOT NULL in the terminal schema, contradicting the baseline. 01.00.00:L100 declares
        // it nvarchar(50) NULL, yet BOTH table rebuilds declare it NOT NULL -- 01.00.05:L18 and
        // 01.00.06:L186 -- and each rebuild drops the real table (01.00.05:L54 and 01.00.06:L227) and
        // renames its temporary copy into place (01.00.05:L57 and 01.00.06:L230).
        builder.Property(u => u.LastName)
            .HasColumnName("LastName")
            .HasMaxLength(50)
            .IsRequired();

        // DisplayName nvarchar(128) NOT NULL with a database default of the empty string: added at
        // 03.02.03:L628-630 as "DisplayName nvarchar(128) NOT NULL CONSTRAINT DF_Users_DisplayName DEFAULT
        // ''", and added a second time behind a version guard at 04.00.04:L666-670 for installations that
        // skipped the earlier script.
        builder.Property(u => u.DisplayName)
            .HasColumnName("DisplayName")
            .HasMaxLength(128)
            .IsRequired()
            .HasDefaultValue(string.Empty);

        // Email nvarchar(256) NULL: 03.00.13:L109-110, populated immediately afterwards from the external
        // membership store at 03.00.13:L113-117.
        builder.Property(u => u.Email)
            .HasColumnName("Email")
            .HasMaxLength(256);

        // IsSuperUser bit NOT NULL with a database default of 0: added at 01.00.02:L242-243, carried
        // through the second rebuild as bit NOT NULL at 01.00.06:L195 with its default re-established at
        // 01.00.06:L201-202, and finally re-tightened at 03.01.01 -- where:L1341-1349 discovers and drops
        // whatever default the column then carried by querying the system catalogue,:L1351 re-declares the
        // column bit NOT NULL, and:L1353 re-adds DF_Users_IsSuperUser DEFAULT (0).
        builder.Property(u => u.IsSuperUser)
            .HasColumnName("IsSuperUser")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        // This column name ends in a LOWER-CASE d. The sole statement that brings it into being is
        // 02.00.00:L6957-6958, "ALTER TABLE Users ADD AffiliateId int NULL", and the terminal view
        // reproduces that spelling at 04.00.04:L782.
        builder.Property(u => u.AffiliateId)
            .HasColumnName("AffiliateId")
            .HasColumnType("int");

        // UpdatePassword bit NOT NULL with a database default of 0: added in the same statement as
        // DisplayName at 03.02.03:L628 and:L631 as "UpdatePassword bit NOT NULL CONSTRAINT
        // DF_Users_UpdatePassword DEFAULT 0", and re-added behind the same version guard at 04.00.04:L668
        // and:L671.
        builder.Property(u => u.UpdatePassword)
            .HasColumnName("UpdatePassword")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        // The external membership store, and why the eleven properties that follow are not columns.
        builder.Ignore(u => u.IsApproved);

        // There is NO Users.CreatedDate column, and mapping one is the likeliest single mistake available
        // on this table.
        builder.Ignore(u => u.CreatedDate);
        builder.Ignore(u => u.IsOnline);
        builder.Ignore(u => u.LastActivityDate);
        builder.Ignore(u => u.LastLockoutDate);
        builder.Ignore(u => u.LastLoginDate);
        builder.Ignore(u => u.LastPasswordChangeDate);
        builder.Ignore(u => u.IsLockedOut);

        // The target stores a one-way hash instead. That concern is owned entirely by
        // Infrastructure/Security/BcryptPasswordHasher.cs, which this file neither references nor
        // duplicates, and the hash is not a column of dbo.Users, so it is unmapped here with the rest.
        builder.Ignore(u => u.PasswordHash);
        builder.Ignore(u => u.PasswordAnswer);
        builder.Ignore(u => u.PasswordQuestion);

        // This unique index MOVED columns mid-history, which is why only its terminal definition means
        // anything.
        builder.HasIndex(u => u.Username)
            .IsUnique()
            .HasDatabaseName("IX_Users");
    }
}
