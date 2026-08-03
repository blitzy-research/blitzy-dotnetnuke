using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

// MIGRATION: everything configured below is the CUMULATIVE TERMINAL state of the DotNetNuke 4.9
// upgrade chain, never the shape of any single script. dbo.UserPortals is the most misleading
// table in the folder to read from its baseline: of the three columns it was created with, one
// was dropped and never returned under that name, and two of the five columns it ends with did
// not exist until three minor versions later. Every statement that ever touched the table was
// enumerated across all 88 scripts under Website/Providers/DataProviders/SqlDataProvider/
// (UnInstall.SqlDataProvider excluded, which names only the surviving constraints), and the
// terminal column set is exactly UserId, PortalId, UserPortalId, CreatedDate and Authorised.
//
// MIGRATION: Rule T4, schema immutability. This configuration binds to objects that already
// exist and describes nothing it may bring into being. It creates, alters and drops no table,
// index or constraint, declares no seed row, and contains no literal SQL beyond the one store
// default expression the schema itself already holds. The physical names of the primary key,
// both foreign keys and both indexes are stated explicitly so the mapping reads against a live
// DotNetNuke database rather than against a name Entity Framework would have invented.
//
// MIGRATION: Rule T7, sentinels. The terminal table has no nullable column, so no property here
// is nullable and no legacy Null.NullInteger, Null.NullDate or Null.NullByte marker is
// reintroduced. One consequence has to be stated rather than assumed: dbo.Portals.PortalID is
// declared IDENTITY(-1, 1) at 01.00.00:L77, so the first tenant is -1 and the second is 0. Both
// values are ordinary, resolvable references in this table, and neither ever means "absent".

/// <summary>
/// Binds the <see cref="UserPortal"/> entity to the existing <c>dbo.UserPortals</c> table - the
/// row that makes one account a member of one tenant.
/// </summary>
/// <remarks>
/// <para>
/// <b>The storage key is not the surrogate, and this is the one mapping in the folder where
/// omitting <c>HasKey</c> would be silently wrong rather than merely implicit.</b>
/// <c>PK_UserPortals</c> is a clustered composite key over <c>UserId</c> and <c>PortalId</c>,
/// declared in the baseline script and never dropped by any upgrade. The <c>UserPortalId</c>
/// identity column arrived two minor versions later, alongside that key rather than in place of
/// it. Entity Framework's by-name convention would select <c>UserPortalId</c> as the primary key
/// if left to itself, and it would do so without a compiler error and without a model-validation
/// error - producing a model whose every write path to this table targets the wrong key. The
/// composite is therefore declared explicitly, and the identity column is mapped as a
/// store-generated non-key value so the provider still reads it back after an insert.
/// </para>
/// <para>
/// The domain entity's <c>Identity</c> projection over <c>UserPortalId</c> is an equality concern
/// and carries no mapping authority; it is naturally unmapped, because it is a get-only
/// expression-bodied member with no setter, so it is not ignored here either.
/// </para>
/// <para>
/// <b>Two spellings are load-bearing and must survive contact with a well-meaning refactor.</b>
/// This table spells its identifier columns <c>UserId</c> and <c>PortalId</c> with a lower-case
/// <c>d</c>, while the tables they reference spell theirs <c>UserID</c> and <c>PortalID</c> with
/// an upper-case <c>D</c> - an asymmetry the terminal foreign-key declarations state outright.
/// And the approval flag is the British <c>Authorised</c>: the American <c>Authorized</c> the
/// baseline created was dropped and never returned. Each <c>HasColumnName</c> below carries its
/// own table's spelling, not a normalised one.
/// </para>
/// <para>
/// <b>Both foreign keys cascade, and that is reproduced rather than softened.</b> The terminal
/// declarations of <c>FK_UserPortals_Users</c> and <c>FK_UserPortals_Portals</c> each specify
/// <c>ON DELETE CASCADE</c>, which is what makes deleting an account or a portal remove the
/// memberships that depend on it. Two cascade paths converging on one table is not a problem to
/// design around: Entity Framework's model validator does not reject them, only the migration
/// generator objects, and the baseline migration for this model is intentionally empty under Rule
/// T4. Downgrading either end to a non-cascading delete behaviour, to quiet a warning that is never
/// raised, would leave orphaned membership rows the database would otherwise have removed.
/// </para>
/// <para>
/// <b>Both store defaults are configured, and both are pinned so they cannot rewrite a caller's
/// value.</b> The terminal schema holds <c>DF_UserPortals_CreatedDate DEFAULT (getdate())</c> and
/// <c>DF_UserPortals_Authorised DEFAULT 1</c>, so recording them keeps the model an honest
/// description of the database. Recording a default, however, makes Entity Framework treat the
/// property as store-generated on insert and omit it from the statement whenever its value equals
/// the property sentinel. For a <see cref="bool"/> that sentinel is <see langword="false"/>, so an
/// unpinned default of <see langword="true"/> would turn a deliberate request for an unauthorised
/// membership into an authorised one. <c>ValueGeneratedNever</c> is therefore applied to both
/// properties: the default stays described, and the value the caller supplied is always the value
/// written.
/// </para>
/// </remarks>
internal sealed class UserPortalConfiguration : IEntityTypeConfiguration<UserPortal>
{
    /// <summary>
    /// Applies the mapping.
    /// </summary>
    /// <param name="builder">The builder for the membership entity type.</param>
    public void Configure(EntityTypeBuilder<UserPortal> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Website/release.config:L354 registers the SQL data provider with objectQualifier="" and
        // :L355 with databaseOwner="dbo", so the physical object is the unqualified UserPortals
        // under the dbo schema. Every physical name stated in this file follows from that pair.
        builder.ToTable("UserPortals", "dbo");

        // MIGRATION: the physical primary key is the composite (UserId, PortalId), created with
        // the table at 01.00.00:L405-411 as
        //     ALTER TABLE dbo.UserPortals WITH NOCHECK ADD
        //         CONSTRAINT PK_UserPortals PRIMARY KEY CLUSTERED ( UserId, PortalId ) ON PRIMARY
        // and never dropped: an exhaustive search of all 88 scripts finds no DROP of
        // PK_UserPortals outside UnInstall.SqlDataProvider, which names it among the constraints
        // an uninstall has to remove and so corroborates that it survives.
        //
        // UserPortalId int NOT NULL IDENTITY (1, 1) was added LATER, at 02.00.00:L7209-7210 -
        // after this key already existed - and was never promoted to be the key. The domain
        // entity's inherited Entity<int>.Identity projection over UserPortalId therefore does NOT
        // replace the legacy composite key; it only gives the entity a single scalar to compare.
        //
        // HasKey is mandatory here, not stylistic. Without it Entity Framework's by-name
        // convention picks UserPortalId, and it does so with no compile error and no
        // model-validation error - the wrong key would reach a live installation unannounced.
        // Column order matters too: UserId leads the clustered key.
        builder.HasKey(m => new { m.UserId, m.PortalId }).HasName("PK_UserPortals");

        // MIGRATION: lower-case `d`. This table declares UserId int NOT NULL at 01.00.00:L154,
        // while dbo.Users declares its key column UserID with an upper-case D. The terminal
        // foreign key at 03.00.13:L44-52 states the asymmetry in one line -
        // FOREIGN KEY (UserId) REFERENCES Users (UserID) - so each side keeps its own spelling and
        // this HasColumnName carries the spelling of the table being configured.
        //
        // The sentinel is left at the CLR default deliberately, unlike PortalId below.
        // dbo.Users.UserID is IDENTITY(1, 1) at 01.00.00:L98, so zero is never a real account and
        // its use as the "not supplied yet" marker for key propagation is exactly right.
        builder.Property(m => m.UserId)
            .HasColumnName("UserId")
            .HasColumnType("int")
            .IsRequired();

        // MIGRATION: lower-case `d` again - PortalId int NOT NULL at 01.00.00:L155, against
        // dbo.Portals.PortalID with an upper-case D, as the terminal foreign key at
        // 01.00.05:L1507-1515 shows.
        //
        // MIGRATION: the sentinel is moved off zero, and it is not cosmetic. This property is part
        // of the primary key AND part of the foreign key to Portals, and for such a property the
        // provider reads a value equal to the CLR default as "not supplied yet, the principal row
        // must still be inserted". dbo.Portals.PortalID is IDENTITY(-1, 1) at 01.00.00:L77, so the
        // first tenant of an installation is -1 and the second is zero - a perfectly ordinary
        // tenant whose members could otherwise never be recorded, because -1 is simultaneously the
        // legacy Null.NullInteger marker. Declaring a sentinel outside the range of real
        // identifiers restores the schema's own meaning: zero identifies a portal, it does not
        // mean "absent". The same reasoning is applied to ModuleSettings.ModuleID, whose table
        // seeds at zero for the same reason.
        builder.Property(m => m.PortalId)
            .HasColumnName("PortalId")
            .HasColumnType("int")
            .HasSentinel(int.MinValue)
            .IsRequired();

        // MIGRATION: a real database-generated IDENTITY(1, 1) column that is deliberately NOT a key
        // participant. 02.00.00:L7209-7210 reads
        //     ALTER TABLE UserPortals ADD UserPortalId int NOT NULL IDENTITY (1, 1)
        // and nothing afterwards moves the primary key onto it. It is mapped, it is store
        // generated - so ValueGeneratedOnAdd keeps Entity Framework from trying to insert a value
        // into an identity column and makes it read the assigned value back - and it is absent
        // from HasKey above. The seed and increment are preserved through UseIdentityColumn so the
        // model records the column as the schema actually declares it.
        //
        // Note the distinction from the CLR member of the same idea: Entity<int>.Identity is an
        // unmapped get-only projection over this property, whereas this property is the real,
        // mapped, generated, non-key column. Both facts hold at once.
        builder.Property(m => m.UserPortalId)
            .HasColumnName("UserPortalId")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // MIGRATION: this is the ONLY creation timestamp in the user model, and the column left and
        // came back. dbo.Users.CreatedDate was dropped at 01.00.02:L282-283 and never returned -
        // but only after 01.00.02:L254-256 had added this column and its last-sign-in companion to
        // this table and :L259-280 had copied every value across, which is why the stamp is
        // per-membership rather than per-account. Both of those columns were then dropped outright
        // at 02.02.01:L54-55, and CreatedDate alone was re-added behind a probe at 03.00.10:L11-15 as
        // "CreatedDate datetime DEFAULT getDate() NOT NULL". 03.01.01:L1325-1337 settled its
        // terminal form: the auto-named default constraint is removed through dynamic SQL, the
        // column is re-asserted NOT NULL at :L1335, and the named
        // DF_UserPortals_CreatedDate DEFAULT (getdate()) is added at :L1337.
        //
        // Its companion did NOT come back. No script after 02.02.01 re-adds the last-sign-in stamp
        // to this table, so there is no such column in the terminal schema and none is mapped here;
        // the domain entity does not declare the property either, so there is nothing to ignore.
        // Every later mention of it in the scripts sits inside a procedure or view body written
        // before that drop and replaced afterwards, which is exactly the trap the terminal-state
        // rule exists to catch.
        //
        // The column type is stated as datetime and must never be promoted to SQL Server's newer
        // higher-precision variant: datetime has a resolution of roughly 3.33 milliseconds, so a
        // promoted mapping would round-trip values the column cannot store and would compare
        // unequal to what the database returns.
        //
        // The default is a server function, so it is expressed as SQL rather than as a constant -
        // but ValueGeneratedNever pins the property so Entity Framework still writes the value the
        // caller supplied from the injected clock. Without that pin a caller who left the property
        // at DateTime.MinValue - which is also the legacy Null.NullDate marker - would silently get
        // the server's clock instead, and, on any installation whose column carries no default, an
        // insert that fails outright.
        builder.Property(m => m.CreatedDate)
            .HasColumnName("CreatedDate")
            .HasColumnType("datetime")
            .IsRequired()
            .HasDefaultValueSql("getdate()")
            .ValueGeneratedNever();

        // MIGRATION: British spelling, and it is load-bearing. The baseline created this flag
        // American-spelled and nullable - "Authorized bit NULL" at 01.00.00:L156 - and
        // 02.02.01:L54-55 DROPPED it. The terminal column is
        //     ALTER TABLE UserPortals ADD Authorised bit NOT NULL
        //         CONSTRAINT DF_UserPortals_Authorised DEFAULT 1
        // added at 03.02.03:L639-640 and re-added under a version guard at 04.00.04:L678-685 for
        // installations whose lineage skipped that script. Authorised is the only spelling present
        // in the terminal schema. Binding the dropped American spelling would fail against every
        // installation in the field, and so would binding the CLR property name - the property is
        // called IsAuthorised, the column is not.
        //
        // The store default is recorded because the schema really holds it, and then pinned with
        // ValueGeneratedNever because recording it is otherwise unsafe: a configured default makes
        // Entity Framework omit the column from an insert whenever the value equals the property
        // sentinel, and for bool that sentinel is false. Unpinned, a caller asking for an
        // unauthorised membership would have the request quietly reversed into an authorised one -
        // the flag the whole table exists to carry. Pinned, the default stays described and false
        // is written as false.
        builder.Property(m => m.IsAuthorised)
            .HasColumnName("Authorised")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(true)
            .ValueGeneratedNever();

        // MIGRATION: the two index names are CROSSED relative to the columns they cover, and the
        // pairing is easy to reverse by accident. 03.00.09:L406 and :L408 drop the pair that
        // 01.00.10:L759-773 had created, and :L410-411 recreate them in their terminal form:
        //     CREATE NONCLUSTERED INDEX IX_UserPortals   ON UserPortals (PortalId)
        //     CREATE NONCLUSTERED INDEX IX_UserPortals_1 ON UserPortals (UserId)
        // So the unsuffixed name covers PortalId and the _1 suffix covers UserId. Neither is
        // UNIQUE - the composite primary key already enforces the only uniqueness this table has,
        // and marking either unique would forbid an account from joining a second portal. The
        // physical names are preserved because the object qualifier is empty.
        builder.HasIndex(m => m.PortalId).HasDatabaseName("IX_UserPortals");
        builder.HasIndex(m => m.UserId).HasDatabaseName("IX_UserPortals_1");

        // MIGRATION: both terminal foreign keys declare ON DELETE CASCADE and both are reproduced
        // faithfully. FK_UserPortals_Users reached its terminal form at 03.00.13:L44-52 -
        //     ALTER TABLE UserPortals WITH NOCHECK ADD CONSTRAINT FK_UserPortals_Users
        //         FOREIGN KEY (UserId) REFERENCES Users (UserID)
        //         ON DELETE CASCADE NOT FOR REPLICATION
        // after being dropped and recreated four times - and FK_UserPortals_Portals at
        // 01.00.05:L1507-1515 in the same shape against Portals (PortalID).
        //
        // Two cascade paths converging here is acceptable and is not worked around. Entity
        // Framework's model validator does not reject multiple cascade paths; only the migration
        // generator objects, and the baseline migration for this model is intentionally empty under
        // Rule T4, so nothing ever asks it to. Downgrading either end to a non-cascading delete
        // behaviour would leave behind membership rows the database itself removes.
        //
        // WITH NOCHECK and NOT FOR REPLICATION have no Entity Framework equivalent and are
        // deliberately unrepresented, here and throughout this folder.
        //
        // Both relationships are owned HERE, on the dependent, and configured exactly once:
        // UserConfiguration and PortalConfiguration deliberately declare none. Applying
        // configurations from an assembly gives no ordering guarantee, so a relationship written
        // from both ends would take whichever delete behaviour happened to run last, with nothing
        // in the build or the model validation to say so. No IsRequired is stated on either
        // relationship: the foreign-key properties are non-nullable int and are key columns, from
        // which Entity Framework already derives that both principals are required.
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
