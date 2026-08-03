using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="UserProfileValue"/> entity to the legacy <c>dbo.UserProfile</c> table.
/// </summary>
/// <remarks>
/// <para>
/// The table is named <c>UserProfile</c> - singular, and neither a pluralisation nor a suffixing of
/// the entity name - so naming it is the first thing this configuration does. Convention would
/// derive a plural name that no object in this schema answers to, and nothing in the build would
/// notice: the model would compile, validate, and then fail at run time against a table that does
/// not exist. The entity is named for what a row actually holds, one profile value, because the
/// table's singular name describes a whole profile while a row is a single answer.
/// </para>
/// <para>
/// The table gives each value two possible homes. The bounded column holds 3750 characters and the
/// overflow column is a long-text type, and the legacy write procedure fills exactly one of the two
/// per row on a length test, nulling the other. Both are mapped raw and the entity derives nothing
/// from them, so there is no computed member to exclude here; the effective value - the bounded
/// column when it is not null, the overflow column otherwise, exactly as the legacy
/// <c>GetUserProfile</c> procedure returns it - is projected by the Application mapper instead.
/// Dropping either column would silently lose one half of everything the table can hold.
/// </para>
/// <para>
/// Both foreign keys cascade on delete, so deleting an account or a definition removes the values
/// that referenced it. The definition cascade is the reason the repository's definition-removal path
/// does not delete values itself: the database already does, and duplicating the work would either
/// be redundant or, if the two disagreed, wrong.
/// </para>
/// <para>
/// <see cref="UserProfileValue.LastUpdatedDate"/> is required with no store default, so every write
/// must supply it. The user service takes it from the injected clock rather than from the database,
/// which keeps the value deterministic under test.
/// </para>
/// <para>
/// <b>Two relationships are declared here, and exactly two.</b> This entity carries both key
/// columns, so it is the dependent half of both relationships and therefore owns both.
/// <c>UserConfiguration</c> deliberately declares no relationship at all, and
/// <c>ProfilePropertyDefinitionConfiguration</c> deliberately leaves its <c>ProfileValues</c>
/// collection unconfigured, because that relationship is owned here.
/// </para>
/// </remarks>
internal sealed class UserProfileValueConfiguration : IEntityTypeConfiguration<UserProfileValue>
{
    /// <summary>
    /// Applies the mapping.
    /// </summary>
    /// <param name="builder">The builder for the profile value entity type.</param>
    public void Configure(EntityTypeBuilder<UserProfileValue> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // MIGRATION: the legacy table is the SINGULAR "UserProfile" - CREATE TABLE UserProfile at
        //   04.00.04:L1411 - while the destination entity is UserProfileValue, named to separate the
        //   per-user VALUE row from the definition row that describes it. Neither the entity name nor
        //   its pluralisation yields the correct table, so the name is stated rather than derived:
        //   convention would produce a plural that does not exist in this schema, and the mismatch
        //   would surface only when a query ran.
        //
        // MIGRATION: the create block is emitted TWICE and the pair is ONE logical change. It appears
        //   at 03.02.03:L1364 and again, identically and still wrapped in
        //   IF NOT EXISTS (select * from dbo.sysobjects where id = object_id(N'UserProfile') and
        //   OBJECTPROPERTY(id, N'IsTable') = 1), at 04.00.04:L1411 - a consolidated re-issue for
        //   installations that skipped the 3.x script, not a second table. The 4.x line number is the
        //   3.x number plus 47 across that block, and both are cited below so every mapping is
        //   traceable from either upgrade line. The index duplicates the same way and is cited where
        //   it is declared.
        //
        // MIGRATION: this table is NEVER altered after creation. A case-insensitive sweep of all 87
        //   upgrade scripts - the uninstall script excluded, normalised for CRLF line endings, for
        //   the templated {databaseOwner} and {objectQualifier} prefixes and for the bracketed
        //   spelling, since a naive search returns nothing where the object exists - finds only the
        //   four ALTER TABLE statements inside those two guarded blocks, which add the primary key
        //   and the two foreign keys. There is no column widening, no type change and no drop
        //   anywhere in the chain, so the CREATE TABLE block IS the terminal definition rather than a
        //   way-point.
        //
        // release.config:L354 sets objectQualifier to the empty string and :L355 sets databaseOwner
        // to dbo, so the physical name carries no prefix and resolves in the dbo schema.
        builder.ToTable("UserProfile", "dbo");

        // MIGRATION: the primary key is declared PRIMARY KEY NONCLUSTERED
        //   (04.00.04:L1422-1423 = 03.02.03:L1375-1376), and no later script drops it. Both the
        //   constraint NAME and the CLUSTERING are expressed, so the model carries what the database
        //   already has instead of what convention would derive: the SQL Server provider defaults a
        //   primary key to CLUSTERED, and the model snapshot is the artefact every future migration is
        //   diffed against, so an unstated clustering is an untrue one rather than an absent one.
        builder.HasKey(v => v.ProfileId).HasName("PK_UserProfile").IsClustered(false);

        // 04.00.04:L1413 (= 03.02.03:L1366) - ProfileID int IDENTITY(1,1) NOT NULL. Note the
        // upper-case ID suffix. The seed is recorded so a generated script would continue the
        // existing sequence rather than restart it.
        //
        // This identity seeds at 1, which makes it the ordinary case: Portals seeds at -1 and Roles,
        // Tabs and Modules at 0, so for those a default int is a genuine key. That is a property of
        // this table alone and is not licence to read 0 or -1 as absence anywhere else - persisted
        // state is declared through Entity<TId>.MarkIdentityPersisted by code that already knows the
        // row exists, never deduced from a value. Nothing in persistence declares it: materialising a
        // row through this mapping leaves IdentityIsPersisted false, so two instances of one stored
        // value compare by object reference.
        builder.Property(v => v.ProfileId)
            .HasColumnName("ProfileID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // 04.00.04:L1414 (= 03.02.03:L1367) - UserID int NOT NULL.
        //
        // The column spells its suffix ID in upper case, and so does the definition reference below.
        // Casing is per-table in this schema and is not a house style to normalise: dbo.UserPortals
        // spells the very same account reference with a lower-case d. This table's own DDL spelling
        // is what is carried here, and one wrong character binds the model to a column that does not
        // exist.
        builder.Property(v => v.UserId)
            .HasColumnName("UserID")
            .HasColumnType("int")
            .IsRequired();

        // 04.00.04:L1415 (= 03.02.03:L1368) - PropertyDefinitionID int NOT NULL. Upper-case ID
        // suffix again, matching the principal column of the same name on
        // dbo.ProfilePropertyDefinition.
        //
        // MIGRATION: this column is what makes the legacy fixed property surface unnecessary.
        //   UserProfile.vb declared nineteen named properties - Cell at L103 through Website at L451
        //   - fifteen of which were accessor pairs over a pre-generics definition collection reached
        //   through GetPropertyValue at L507. Which property a value belongs to is DATA here, not
        //   shape, so a tenant may declare any set of definitions and this one table carries the
        //   answers to all of them. None of those nineteen names is mapped, and none may be added
        //   back: no column exists for any of them, and a fixed surface could not represent the
        //   moment a tenant declared one property more.
        builder.Property(v => v.PropertyDefinitionId)
            .HasColumnName("PropertyDefinitionID")
            .HasColumnType("int")
            .IsRequired();

        // 04.00.04:L1416 (= 03.02.03:L1369) - PropertyValue nvarchar(3750) NULL. The exact bound is
        // carried rather than rounded. The column is Unicode, which is already this provider's
        // default for a string, so no narrowing to a single-byte encoding is asserted; and it is
        // nullable, so it is deliberately NOT marked required.
        //
        // MIGRATION: the 3750 is load-bearing rather than arbitrary. The legacy write procedure
        //   stores the submitted value here only while DATALENGTH of the parameter is 7500 or less,
        //   and nulls this column otherwise (04.00.04:L1626 and L1647). The parameter is a long-text
        //   type and the data is UCS-2, so DATALENGTH counts two bytes per character and the
        //   7500-byte threshold is exactly this column's 3750-character capacity: the split fires
        //   precisely when the value would not fit.
        //
        // MIGRATION: null here means SQL NULL and nothing else. The legacy null-string sentinel was
        //   the EMPTY STRING - UserProfile.vb:L509 opens GetPropertyValue by seeding its local with
        //   exactly that sentinel - so a legacy read could not distinguish a column that had never
        //   been written from one deliberately blanked. The destination keeps null and the empty
        //   string distinct and installs no null-to-sentinel value converter in either direction.
        //   Normalising a null to an empty string here would be worse than untidy: the read procedure
        //   branches on this column being null to decide whether to fall back to the overflow column
        //   (04.00.04:L1592), so an empty string written in place of a null would suppress that
        //   fallback and lose a long value outright.
        builder.Property(v => v.PropertyValue)
            .HasColumnName("PropertyValue")
            .HasMaxLength(3750);

        // MIGRATION: PropertyText is a REAL terminal column and must never be dropped
        //   (04.00.04:L1417 = 03.02.03:L1370, declared as a nullable long-text column). It holds the
        //   long-form answer beside the short 3750-character column, and three independent facts make
        //   it load-bearing: it is declared in the create block; the write procedure diverts every
        //   value longer than the bounded column into it and nulls the other
        //   (04.00.04:L1627 and L1648), so for a long answer this is the ONLY place the data exists;
        //   and the search procedure matches both raw columns in a single OR (04.00.04:L946), so a
        //   model that collapsed them would silently stop finding long answers. It is easy to
        //   overlook precisely because the legacy business object surfaced one logical value.
        //
        // MIGRATION: the store type is pinned explicitly and NO length facet is set alongside it.
        //   This type carries no length, so combining the two would be a modelling error, and leaving
        //   the type to convention would produce an unbounded national character varying column - a
        //   different column type in a schema this migration is forbidden to alter. Nullable, so no
        //   requiredness is asserted either.
        builder.Property(v => v.PropertyText)
            .HasColumnName("PropertyText")
            .HasColumnType("ntext");

        // MIGRATION: Visibility is a PROFILE-visibility integer with no enumeration in the domain,
        //   and it is deliberately NOT the module-visibility enumeration. That type belongs
        //   exclusively to TabModule.Visibility and is mapped only by TabModuleConfiguration; it is
        //   not referenced here, no value converter is applied to this column, and this file imports
        //   no enumeration namespace at all.
        //
        //   The legacy reader did cast this column to a user-visibility enumeration, but that
        //   enumeration names only three members while the code interpreting the stored number folds
        //   0, 1, 2 and the legacy -1 absent-integer marker onto those three and leaves every other
        //   stored integer unmapped (ProfilePropertyDefinition.vb:L354-358). Declaring an enumeration
        //   here would publish that gap as though it were a complete contract and would let a number
        //   the database genuinely holds arrive as an undeclared member, so the raw persisted integer
        //   is preserved and its interpretation belongs to the layer that presents it.
        //
        // 04.00.04:L1418 (= 03.02.03:L1371) - Visibility int NOT NULL DEFAULT 0. This is the ONLY
        // default-valued column on the table, and no default is invented for any other. Declaring the
        // default here is what hands the value back to the database: the provider then omits the
        // column from an insert that carries the CLR default and lets the store supply it, which is
        // exactly what the legacy upgrade relied on when it moved the flat address columns into this
        // table and inserted only the account, the definition, the value and the timestamp
        // (03.02.03:L2094). Restating the number as a property initialiser on the entity instead
        // would write it on every insert and stop it being the database's answer.
        builder.Property(v => v.Visibility)
            .HasColumnName("Visibility")
            .HasColumnType("int")
            .IsRequired()
            .HasDefaultValue(0);

        // MIGRATION: LastUpdatedDate is the legacy datetime type (04.00.04:L1419 = 03.02.03:L1372,
        //   LastUpdatedDate datetime NOT NULL). The store type is pinned explicitly because
        //   convention maps a CLR date to this provider's newer high-precision date type instead, and
        //   the two differ in range, precision and storage width, so letting convention choose would
        //   describe a column this schema does not have.
        //
        //   Required, and with NO default of any kind: the column declares none, so every insert must
        //   carry a value and no server-side default expression is configured. The caller assigns it
        //   from the injected clock, which is what makes a write deterministic under test; the legacy
        //   provider read the local server time at the call site instead. It is also the only audit
        //   column this table has - there is no created-on, created-by or updated-by to pair it with
        //   - which is why the entity is a plain one rather than an audited one.
        builder.Property(v => v.LastUpdatedDate)
            .HasColumnName("LastUpdatedDate")
            .HasColumnType("datetime")
            .IsRequired();

        // 03.03.02:L137-138, re-issued identically at 04.03.02:L138-139 -
        //     IF NOT EXISTS ( SELECT * FROM sysindexes WHERE name = N'IX_UserProfile')
        //         CREATE NONCLUSTERED INDEX IX_UserProfile ON UserProfile (UserID) ON PRIMARY
        // Existence-guarded in both scripts, so the pair is ONE logical change, and never dropped.
        // The physical name is preserved rather than left to convention, which would append the
        // column name and so describe an index the legacy database does not have. It supports the
        // repository's read of every value belonging to one account, which filters on that column
        // alone.
        //
        // Deliberately NOT unique, and no uniqueness is asserted anywhere in this file. The negative
        // sweep matters as much as the positive one: across all 87 scripts this table carries no
        // unique index and no unique constraint of any kind, and in particular none over the account
        // and definition pair together - however natural that pair looks for a key-and-value table,
        // and however plainly it reads as the natural key. Asserting a uniqueness the database does
        // not have would make the change tracker reject rows the database accepts.
        //
        // The terminal object set for this table is exactly PK_UserProfile, IX_UserProfile,
        // FK_UserProfile_Users and FK_UserProfile_ProfilePropertyDefinition. Nothing else.
        builder.HasIndex(v => v.UserId).HasDatabaseName("IX_UserProfile");

        // MIGRATION: both terminal foreign keys declare ON DELETE CASCADE explicitly and both are
        //   reproduced faithfully, at 04.00.04:L1425-1429 (= 03.02.03:L1378-1382):
        //       ALTER TABLE UserProfile WITH NOCHECK ADD CONSTRAINT FK_UserProfile_Users
        //           FOREIGN KEY(UserID) REFERENCES Users (UserID) ON DELETE CASCADE
        //       ALTER TABLE UserProfile WITH NOCHECK
        //           ADD CONSTRAINT FK_UserProfile_ProfilePropertyDefinition
        //           FOREIGN KEY(PropertyDefinitionID)
        //           REFERENCES ProfilePropertyDefinition (PropertyDefinitionID) ON DELETE CASCADE
        //
        //   Neither is softened to a non-cascading delete behaviour to avoid multiple cascade paths.
        //   This entity genuinely sits where two cascade chains converge -
        //   Portals to ProfilePropertyDefinition to UserProfile, and Users to UserProfile - and that
        //   is exactly what the legacy schema declares. The model validator does not reject multiple
        //   cascade paths; only the migration generator objects, and the baseline migration for this
        //   model carries a deliberately empty body under Rule T4, so nothing ever asks it to.
        //   Weakening either end would leave behind value rows the database itself removes, and would
        //   let the model attempt writes the database refuses.
        //
        //   The definition cascade is also why a definition is withdrawn by flag rather than deleted
        //   - see ProfilePropertyDefinition.IsDeleted - since removing one would take every account's
        //   answer with it.
        //
        //   WITH NOCHECK and NOT FOR REPLICATION have no counterpart in the model and are left
        //   unrepresented, here and across this folder.
        //
        //   Both relationships are owned HERE, on the dependent, and each is configured exactly once.
        //   Applying configurations from an assembly gives no ordering guarantee, so a relationship
        //   restated from its principal side would take whichever delete behaviour happened to run
        //   last, with nothing in the build or in model validation to say so - which is why
        //   UserConfiguration declares no relationship and ProfilePropertyDefinitionConfiguration
        //   leaves its ProfileValues collection unconfigured. No requiredness is stated on either
        //   relationship: both foreign-key properties are non-nullable int, from which the model
        //   already derives that both principals are required. Both keys reuse the columns mapped
        //   above, so no shadow property is created for either.
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

        // MIGRATION: nothing else is mapped and nothing is ignored. The table has exactly the seven
        //   columns above and is never altered, so there is no dropped-column history to guard
        //   against, and the entity declares no member beyond those seven, its two navigations and
        //   the identity override - a get-only expression-bodied property with no setter and no
        //   backing field, which the model's writability requirement excludes on its own, so nothing
        //   is written to exclude it.
        //
        // MIGRATION: the legacy UserProfile business object flattened DEFINITION metadata and
        //   per-user VALUES into one shape; the destination splits them on the physical table
        //   boundary. So no definition-side member appears here - a property's name, its category,
        //   its editor type, its length, its requiredness, its visibility, its validation pattern,
        //   its view order and its fallback value all live on dbo.ProfilePropertyDefinition and are
        //   mapped by ProfilePropertyDefinitionConfiguration - and no account display field appears
        //   here either, since the user name, display name and e-mail address belong to dbo.Users.
        //   The legacy pre-generics definition-collection wrapper produces no target type at all.
        //
        // MIGRATION: the legacy read path built these values by hand from a forward-only reader, so
        //   no reflection-based row hydrator, no hand-rolled population routine, no hydration
        //   interface and no sentinel translation survives the port; the relational materialiser does
        //   that work now.
    }
}
