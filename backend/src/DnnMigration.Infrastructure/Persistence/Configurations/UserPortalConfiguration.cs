using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

// Everything configured below is the CUMULATIVE TERMINAL state of the DotNetNuke 4.9 upgrade chain, never
// the shape of any single script. dbo.UserPortals is the most misleading table in the folder to read from
// its baseline: of the three columns it was created with, one was dropped and never returned under that
// name, and two of the five columns it ends with did not exist until three minor versions later.

/// <summary>
/// Binds the <see cref="UserPortal"/> entity to the existing <c>dbo.UserPortals</c> table - the row that
/// makes one account a member of one tenant.
/// </summary>
/// <remarks>
/// <para>
/// <b>The storage key is not the surrogate, and this is the one mapping in the folder where omitting
/// <c>HasKey</c> would be silently wrong rather than merely implicit.</b> <c>PK_UserPortals</c> is a
/// clustered composite key over <c>UserId</c> and <c>PortalId</c>, declared in the baseline script and
/// never dropped by any upgrade.
/// </para>
/// <para>
/// The domain entity's <c>Identity</c> projection over <c>UserPortalId</c> is an equality concern and
/// carries no mapping authority; it is naturally unmapped, because it is a get-only expression-bodied
/// member with no setter, so it is not ignored here either.
/// </para>
/// </remarks>
internal sealed class UserPortalConfiguration : IEntityTypeConfiguration<UserPortal>
{
    /// <summary>Applies the mapping.</summary>
    /// <param name="builder">The builder for the membership entity type.</param>
    public void Configure(EntityTypeBuilder<UserPortal> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Website/release.config:L354 registers the SQL data provider with objectQualifier="" and:L355 with
        // databaseOwner="dbo", so the physical object is the unqualified UserPortals under the dbo schema.
        // Every physical name stated in this file follows from that pair.
        builder.ToTable("UserPortals", "dbo");

        // The physical primary key is the composite (UserId, PortalId), created with the table at
        // 01.00.00:L405-411 as ALTER TABLE dbo.UserPortals WITH NOCHECK ADD CONSTRAINT PK_UserPortals
        // PRIMARY KEY CLUSTERED ( UserId, PortalId ) ON PRIMARY and never dropped: an exhaustive search of
        // all 88 scripts finds no DROP of PK_UserPortals outside UnInstall.SqlDataProvider, which names it
        // among the constraints an uninstall has to remove and so corroborates that it survives.
        builder.HasKey(m => new { m.UserId, m.PortalId }).HasName("PK_UserPortals");

        // Lower-case `d`.
        builder.Property(m => m.UserId)
            .HasColumnName("UserId")
            .HasColumnType("int")
            .IsRequired();

        // Lower-case `d` again - PortalId int NOT NULL at 01.00.00:L155, against dbo.Portals.PortalID with
        // an upper-case D, as the terminal foreign key at 01.00.05:L1507-1515 shows.
        builder.Property(m => m.PortalId)
            .HasColumnName("PortalId")
            .HasColumnType("int")
            .HasSentinel(int.MinValue)
            .IsRequired();

        // A real database-generated IDENTITY(1, 1) column that is deliberately NOT a key participant.
        // 02.00.00:L7209-7210 reads ALTER TABLE UserPortals ADD UserPortalId int NOT NULL IDENTITY (1, 1)
        // and nothing afterwards moves the primary key onto it.
        builder.Property(m => m.UserPortalId)
            .HasColumnName("UserPortalId")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // MIGRATION: this is the ONLY creation timestamp in the user model, and the column left and came
        // back. dbo.Users.CreatedDate was dropped at 01.00.02:L282-283 and never returned but only after
        // 01.00.02:L254-256 had added this column and its last-sign-in companion to this table and:L259-280
        // had copied every value across, which is why the stamp is per-membership rather than per-account.
        builder.Property(m => m.CreatedDate)
            .HasColumnName("CreatedDate")
            .HasColumnType("datetime")
            .IsRequired()
            .HasDefaultValueSql("getdate()")
            .ValueGeneratedNever();

        // British spelling, and it is load-bearing. The baseline created this flag American-spelled and
        // nullable - "Authorized bit NULL" at 01.00.00:L156 - and 02.02.01:L54-55 DROPPED it.
        builder.Property(m => m.IsAuthorised)
            .HasColumnName("Authorised")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(true)
            .ValueGeneratedNever();

        // The two index names are CROSSED relative to the columns they cover, and the pairing is easy to
        // reverse by accident. 03.00.09:L406 and:L408 drop the pair that 01.00.10:L759-773 had created,
        // and:L410-411 recreate them in their terminal form: CREATE NONCLUSTERED INDEX IX_UserPortals ON
        // UserPortals (PortalId) CREATE NONCLUSTERED INDEX IX_UserPortals_1 ON UserPortals (UserId) So the
        // unsuffixed name covers PortalId and the _1 suffix covers UserId.
        builder.HasIndex(m => m.PortalId).HasDatabaseName("IX_UserPortals");
        builder.HasIndex(m => m.UserId).HasDatabaseName("IX_UserPortals_1");

        // Both terminal foreign keys declare ON DELETE CASCADE and both are reproduced faithfully.
        builder.HasOne(m => m.User)
            .WithMany(u => u.UserPortals)
            .HasForeignKey(m => m.UserId)
            .HasConstraintName("FK_UserPortals_Users")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.Portal)
            .WithMany(p => p.UserPortals)
            .HasForeignKey(m => m.PortalId)
            .HasConstraintName("FK_UserPortals_Portals")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
