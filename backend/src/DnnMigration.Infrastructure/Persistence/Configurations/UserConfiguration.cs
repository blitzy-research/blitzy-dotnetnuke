using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

// MIGRATION: everything configured below is the CUMULATIVE TERMINAL state of the DotNetNuke
// 4.9 upgrade chain, and never the shape of its baseline create script. The 88 scripts under
// Website/Providers/DataProviders/SqlDataProvider/ are append-only AND destructive: dbo.Users
// is created once, rebuilt twice through a Tmp_Users copy-drop-rename, has nine columns
// removed in a single statement, and gains three more afterwards. Reading the baseline alone
// therefore yields both the wrong column set and the wrong nullability. Every fact recorded
// here was re-measured by replaying every "ALTER TABLE Users" statement in version order,
// with the script text normalised for its CRLF line endings, its {databaseOwner} and
// {objectQualifier} templating and its optional bracket quoting -- a naive search reports
// nothing when the object plainly exists, and the terminal constraint names are themselves
// templated. Citations below are script:line.
//
// MIGRATION: nine columns, nine mappings, and eleven properties deliberately left unmapped.
// The terminal table carries exactly nine columns, corroborated independently by the terminal
// vw_Users view at 04.00.04:L771-787, which selects U.UserId, U.Username, U.FirstName,
// U.LastName, U.DisplayName, U.IsSuperUser, U.Email, U.AffiliateId and U.UpdatePassword from
// Users while taking PortalId and Authorised from a left join onto UserPortals. That same
// view is the standing evidence that dbo.Users carries no portal identifier of its own: a
// user is one row shared by every portal it belongs to, and the per-portal facts live on the
// UserPortals row. No portal identifier is mapped here and no portal navigation is declared.
//
// MIGRATION: Rule T4, schema immutability. This configuration binds to objects that already
// exist and describes nothing it may bring into being. It creates, alters and drops no table,
// index or constraint, declares no seed rows, and contains no literal SQL of any kind. The
// strongest justification for that rule anywhere in this schema sits on this very table, and
// it is set down with the unmapped block further below.

/// <summary>
/// Binds <see cref="User"/> to the existing, unaltered DotNetNuke <c>dbo.Users</c> table.
/// </summary>
/// <remarks>
/// <para>
/// Discovery and visibility. This type is <see langword="internal"/> because nothing consumes
/// it directly: the persistence context applies every configuration in this assembly by
/// reflection, activating each one through a parameterless constructor resolved with
/// public-instance binding. This class consequently declares NO constructor at all, so the
/// compiler supplies a public one and the type is found. Adding a private, internal or
/// protected parameterless constructor would render the configuration silently
/// undiscoverable, with neither a compile error nor a model-validation error to reveal it, and
/// on this table that is the worst available failure mode: the eleven properties unmapped
/// below would then stay mapped, and every query against <c>dbo.Users</c> would fail at run
/// time with an invalid column name.
/// </para>
/// <para>
/// Legacy naming is honoured exactly. The legacy provider is registered with an empty object
/// qualifier and <c>dbo</c> as the database owner (<c>Website/release.config</c> lines 354 and
/// 355), so the table is addressed unqualified in the <c>dbo</c> schema and every one of the
/// nine columns carries an explicit name rather than relying on a naming convention. Only the
/// identifier column actually differs from its property name, and then only in the casing of
/// its final letters; the affiliate column is noteworthy for the opposite reason, being the one
/// place where the legacy code and the schema disagree while the schema and the property agree.
/// Each of the two is annotated at its own mapping.
/// </para>
/// <para>
/// Scope. This class configures the table, its key and identity, its nine columns, the eleven
/// properties that are not columns, and its one unique index. It declares no association of
/// any kind, because <see cref="User"/> holds no foreign-key column: every inbound edge --
/// per-portal membership, profile values, role assignments, and the two individually granted
/// permission edges -- is declared once, by the dependent that owns the key. Declaring an edge
/// from both ends is a genuine hazard here rather than a stylistic preference, since the
/// reflection-based application of configurations gives no ordering guarantee: the surviving
/// behaviour would be whichever end happened to run last, and nothing would catch the
/// disagreement at build time.
/// </para>
/// <para>
/// Sentinels are not reinstated (Rule T7). The legacy model encoded absence as -1 for an
/// integer and as the empty string for text, and seeded its fields from that table --
/// <c>Library/Components/Users/UserInfo.vb</c> lines 66 to 69 assign the integer marker to the
/// identifier, the portal identifier and the affiliate field alike. Here a column the schema
/// declares nullable maps to a nullable CLR property and to nothing else: no value converter
/// translates between <see langword="null"/> and a marker value in either direction. Where a
/// legacy marker is externally observable it is preserved by the transfer objects at the API
/// boundary, never in persistence.
/// </para>
/// <para>
/// This class is stateless and holds no field, so a single instance may be applied by any
/// number of model builders concurrently.
/// </para>
/// </remarks>
internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    /// <summary>
    /// Applies the <c>dbo.Users</c> mapping to the supplied entity-type builder.
    /// </summary>
    /// <param name="builder">
    /// The builder for the <see cref="User"/> entity type, supplied by the model-building
    /// pipeline. Never <see langword="null"/>: the framework owns its lifetime and always
    /// passes a live builder, so this method performs no guard of its own, which would only
    /// disguise a defect in the composition root as a recoverable condition.
    /// </param>
    public void Configure(EntityTypeBuilder<User> builder)
    {
        // The table is addressed unqualified in the dbo schema: Website/release.config:L354
        // registers the legacy provider with an empty object qualifier and :L355 registers dbo
        // as the database owner, so no prefix participates in the physical name.
        builder.ToTable("Users", "dbo");

        // MIGRATION: the primary key is CLUSTERED in the terminal schema, not nonclustered as
        // every earlier version of it was. It was declared PK_Users PRIMARY KEY NONCLUSTERED
        // at 01.00.00:L477-481 and re-established in that same form after each table rebuild
        // (01.00.05:L67-71 and 01.00.06:L240-244), then dropped at 03.00.13:L23-24 and
        // recreated as PK_Users PRIMARY KEY CLUSTERED at 03.00.13:L26-30, which is where its
        // history ends. The physical constraint name is preserved so the model names the
        // constraint the database actually holds; clustering itself is a storage decision the
        // model does not express, and this file does not pretend otherwise.
        //
        // The key is stated explicitly on UserId rather than left to convention. The entity's
        // inherited Identity member forwards this same value for equality only: it is
        // get-only and expression-bodied, so it has neither a setter nor a backing field and
        // the property-discovery convention excludes it. It needs no unmapping, and unmapping
        // it would wrongly suggest that it had ever been a candidate for a column.
        builder.HasKey(u => u.UserId).HasName("PK_Users");

        // UserID int NOT NULL IDENTITY(1, 1): 01.00.00:L98, carried unchanged through both
        // rebuilds at 01.00.05:L16 and 01.00.06:L184.
        //
        // The seed is 1, which is worth recording because it is the exception in this schema
        // rather than the rule: Portals seeds its identity at -1 and Roles, Tabs and Modules
        // each seed at 0, so for those aggregates a default-looking value is a real persisted
        // identity. Users are unaffected -- 0 is never a persisted UserID -- and the seed and
        // increment are stated here so that no reader has to infer them and no generated
        // script can drift from them.
        builder.Property(u => u.UserId)
            .HasColumnName("UserID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // Username nvarchar(100) NOT NULL: introduced by the second table rebuild at
        // 01.00.06:L197 and back-filled from the then-existing email column immediately
        // afterwards at 01.00.06:L268-269, which is why long-lived installations hold login
        // names that look like email addresses. No format rule is imposed here, and none may
        // be: 01.00.02:L246-251 identifies the built-in host account by a login value that is
        // not an address at all.
        //
        // MIGRATION: no default value is configured, deliberately. DF_Users_Username DEFAULT
        // N'default' was added at 01.00.06:L205-206 purely so the back-fill could run against
        // existing rows, and it was DROPPED again at 01.00.06:L272-273 within that same
        // script. The terminal column carries no default, so configuring one would invent a
        // constraint the database does not have and would let an incomplete insert succeed
        // where the database would rightly reject it.
        builder.Property(u => u.Username)
            .HasColumnName("Username")
            .HasMaxLength(100)
            .IsRequired();

        // FirstName nvarchar(50) NOT NULL: present since 01.00.00:L99 and unchanged by either
        // rebuild (01.00.05:L17 and 01.00.06:L185). A genuine column of this table, and the
        // terminal view selects it straight from the row at 04.00.04:L777 -- notwithstanding
        // that the legacy property routed every read through a lazily hydrated profile wrapper
        // at Library/Components/Users/UserInfo.vb lines 144 to 151. This configuration maps
        // the table, not the wrapper.
        builder.Property(u => u.FirstName)
            .HasColumnName("FirstName")
            .HasMaxLength(50)
            .IsRequired();

        // MIGRATION: LastName is NOT NULL in the terminal schema, contradicting the baseline.
        // 01.00.00:L100 declares it nvarchar(50) NULL, yet BOTH table rebuilds declare it NOT
        // NULL -- 01.00.05:L18 and 01.00.06:L186 -- and each rebuild drops the real table
        // (01.00.05:L54 and 01.00.06:L227) and renames its temporary copy into place
        // (01.00.05:L57 and 01.00.06:L230). The rebuild is therefore what survives, the
        // entity's non-nullable declaration is correct, and the constraint is stated
        // explicitly here so that nobody re-derives it from the baseline and relaxes it.
        builder.Property(u => u.LastName)
            .HasColumnName("LastName")
            .HasMaxLength(50)
            .IsRequired();

        // DisplayName nvarchar(128) NOT NULL with a database default of the empty string:
        // added at 03.02.03:L628-630 as
        // "DisplayName nvarchar(128) NOT NULL CONSTRAINT DF_Users_DisplayName DEFAULT ''",
        // and added a second time behind a version guard at 04.00.04:L666-670 for
        // installations that skipped the earlier script.
        //
        // The default is the empty string and not null. Because the column is NOT NULL, empty
        // text is the value the database genuinely stores for an unset display name; it is not
        // a legacy marker to be normalised into null, and Rule T7 does not apply to it.
        builder.Property(u => u.DisplayName)
            .HasColumnName("DisplayName")
            .HasMaxLength(128)
            .IsRequired()
            .HasDefaultValue(string.Empty);

        // Email nvarchar(256) NULL: 03.00.13:L109-110, populated immediately afterwards from
        // the external membership store at 03.00.13:L113-117.
        //
        // MIGRATION: this is a REPLACEMENT column rather than the original. An earlier
        // Email nvarchar(100) NOT NULL existed from 01.00.00:L107, was carried at that same
        // width through both rebuilds (01.00.05:L25 and 01.00.06:L193), and was then removed
        // altogether by the nine-column drop at 02.02.01:L50-51. Both the width
        // and the nullability therefore come from the later statement, so the width is 256 and
        // not 100, and no requiredness is declared. Nullable in the schema, nullable in the
        // model, and no marker value substituted for absence.
        builder.Property(u => u.Email)
            .HasColumnName("Email")
            .HasMaxLength(256);

        // IsSuperUser bit NOT NULL with a database default of 0: added at 01.00.02:L242-243,
        // carried through the second rebuild as bit NOT NULL at 01.00.06:L195 with its default
        // re-established at 01.00.06:L201-202, and finally re-tightened at 03.01.01 -- where
        // :L1341-1349 discovers and drops whatever default the column then carried by querying
        // the system catalogue, :L1351 re-declares the column bit NOT NULL, and :L1353 re-adds
        // DF_Users_IsSuperUser DEFAULT (0). This is the flag that decides which store answers
        // for approval, so its non-nullability is load-bearing rather than incidental.
        builder.Property(u => u.IsSuperUser)
            .HasColumnName("IsSuperUser")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        // MIGRATION: this column name ends in a LOWER-CASE d. The sole statement that brings
        // it into being is 02.00.00:L6957-6958, "ALTER TABLE Users ADD AffiliateId int NULL",
        // and the terminal view reproduces that spelling at 04.00.04:L782. The legacy VB
        // property was spelled with a trailing upper-case D instead
        // (Library/Components/Users/UserInfo.vb:L87), and a repository-wide census of the two
        // forms returns 165 of the schema spelling against 18 of the code spelling, every one
        // of the latter a VB identifier rather than DDL. Under the default collation the
        // difference is immaterial to SQL Server, but the mapping carries the spelling the
        // schema uses, because describing the schema faithfully is the entire purpose of this
        // file and a scaffolding comparison would otherwise report a spurious difference.
        //
        // Nullable in the schema, so nullable in the model. The legacy integer marker of -1
        // that UserInfo.vb:L69 seeded into this field is not reinstated (Rule T7): absence is
        // null, and -1 would in any case be indistinguishable from a real affiliate.
        builder.Property(u => u.AffiliateId)
            .HasColumnName("AffiliateId")
            .HasColumnType("int");

        // UpdatePassword bit NOT NULL with a database default of 0: added in the same
        // statement as DisplayName at 03.02.03:L628 and :L631 as
        // "UpdatePassword bit NOT NULL CONSTRAINT DF_Users_UpdatePassword DEFAULT 0", and
        // re-added behind the same version guard at 04.00.04:L668 and :L671.
        //
        // MIGRATION: the legacy model placed this flag on its membership wrapper
        // (Library/Components/Users/Membership/UserMembership.vb:L323) even though it is a real
        // column of this table, which is exactly the kind of misplacement the nine-column
        // audit corrects. It is mapped where the schema puts it, and the terminal view confirms
        // the placement at 04.00.04:L783.
        builder.Property(u => u.UpdatePassword)
            .HasColumnName("UpdatePassword")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        // MIGRATION: the external membership store, and why the eleven properties that follow
        // are not columns. DotNetNuke 4.x relocated credentials and membership state away from
        // dbo.Users and into the ASP.NET 2.0 membership schema, which is keyed by login name
        // rather than by UserID -- every cross-store statement in the chain matches
        // Users.Username against the membership store's own user name, as at
        // 02.02.01:L18-L44 and again in the email back-fill at 03.00.13:L113-117. Those
        // objects are installed by Microsoft's ASP.NET SQL registration payload, shipped beside
        // the upgrade chain as InstallMembership.sql, where for instance
        // aspnet_Membership.IsApproved is declared bit NOT NULL at line 90. The DotNetNuke
        // chain never brings a single one of them into being. It only patches them:
        // 04.00.00:L31 is "ALTER PROCEDURE dbo.aspnet_Membership_UpdateUser" and 04.00.00:L119
        // is "ALTER PROCEDURE dbo.aspnet_Membership_UpdateUserInfo", each grafting DotNetNuke's
        // own failed-attempt and lockout-state bookkeeping onto a procedure it did not write,
        // and a scan of all 88 scripts for a CREATE of any such object returns nothing at all.
        //
        // MIGRATION: that finding is the decisive proof of Rule T4 for this whole schema.
        // Because the chain only patches those objects, replaying all 88 scripts against an
        // empty database CANNOT reproduce the terminal schema, and neither can any script
        // generated from this model. The membership schema is consequently treated as an
        // external legacy dependency that this entity is mapped ALONGSIDE rather than as
        // something the model owns: no entity, no keyless type and no view mapping is
        // introduced here to reach it, and the solution's baseline versioning step -- owned by
        // another file -- is intentionally inert for precisely the same reason.
        //
        // MIGRATION: the eleven values are therefore composed by the user repository and the
        // security services beside it, as one deliberate and inspectable step inside a method,
        // drawing on the membership store, on the per-portal row, or on the target credential
        // store. There is no lazy-hydrating getter: the legacy design fetched on first read
        // from inside a property (UserInfo.vb lines 196 to 204), so reading a property opened a
        // database connection, and Rule T8 deletes that mechanism instead of porting it. A null
        // here means "not composed on this read path", which is the distinction the legacy
        // hydration flag at UserMembership.vb:L243-253 was trying and failing to express.
        //
        // MIGRATION: unmapping all eleven is FUNCTIONALLY REQUIRED, not tidiness. Each has a
        // public setter, so the property-discovery convention WILL bind it to a column that
        // does not exist unless it is excluded here, and a single omission fails every query
        // against this table with an invalid column name -- a failure no compiler can see and
        // no model validation reports. Their legacy origins, all in
        // Library/Components/Users/Membership/UserMembership.vb, are Approved at L83,
        // CreatedDate at L103, IsOnLine at L123, LastActivityDate at L143, LastLockoutDate at
        // L163, LastLoginDate at L183, LastPasswordChangeDate at L203, LockedOut at L223,
        // Password at L263, PasswordAnswer at L283 and PasswordQuestion at L303. The legacy
        // hydration flag beside them has no counterpart in the target at all.
        builder.Ignore(u => u.IsApproved);

        // MIGRATION: there is NO Users.CreatedDate column, and mapping one is the likeliest
        // single mistake available on this table. It existed in the baseline at 01.00.00:L108
        // and was DROPPED, together with LastLoginDate, at 01.00.02:L282-283 -- but only after
        // 01.00.02:L254-256 had added both columns to dbo.UserPortals and :L259-280 had copied
        // every value across, row by row. The creation stamp consequently lives on
        // UserPortals.CreatedDate and is mapped by that entity's own configuration, which is
        // also why the stamp is per-portal rather than per-user. The same holds for the
        // last-sign-in stamp that the very same statement removed, unmapped a few lines below.
        builder.Ignore(u => u.CreatedDate);
        builder.Ignore(u => u.IsOnline);
        builder.Ignore(u => u.LastActivityDate);
        builder.Ignore(u => u.LastLockoutDate);
        builder.Ignore(u => u.LastLoginDate);
        builder.Ignore(u => u.LastPasswordChangeDate);
        builder.Ignore(u => u.IsLockedOut);

        // MIGRATION: the credential store changed, and this configuration maps no credential
        // column of any kind. The baseline held a plaintext Password nvarchar(20) NOT NULL
        // directly on dbo.Users at 01.00.00:L106; both rebuilds widened it to nvarchar(50)
        // (01.00.05:L24 and 01.00.06:L192); and the nine-column drop at 02.02.01:L50-51 removed
        // it for good. The legacy membership provider then held passwords REVERSIBLY --
        // Website/release.config:L245 sets the storage format to encrypted and :L239 enables
        // password retrieval, against a symmetric key committed to source control at
        // release.config:L89-L93 -- so recovering any password required only the repository.
        //
        // MIGRATION: the target stores a one-way hash instead. That concern is owned entirely
        // by Infrastructure/Security/BcryptPasswordHasher.cs, which this file neither
        // references nor duplicates, and the hash is not a column of dbo.Users, so it is
        // unmapped here with the rest. Password RETRIEVAL is deliberately not carried forward to
        // any endpoint or screen. The legacy password POLICY is preserved rather than tightened
        // mid-migration -- release.config:L242 requires a minimum length of 7, :L243 requires no
        // non-alphanumeric characters and :L241 requires no question and answer -- because
        // hardening it here would exclude accounts that are valid today.
        //
        // MIGRATION: credential migration does not add columns to dbo.Users. The external membership
        // store remains the source of Password, PasswordFormat and PasswordSalt; MembershipStore reads
        // those fields for the isolated legacy verifier, and AuthService immediately replaces an
        // accepted legacy value with BCrypt. Administrative reset remains the fallback. This entity
        // configuration continues to ignore credential properties because none belongs to dbo.Users.
        builder.Ignore(u => u.PasswordHash);
        builder.Ignore(u => u.PasswordAnswer);
        builder.Ignore(u => u.PasswordQuestion);

        // MIGRATION: this unique index MOVED columns mid-history, which is why only its
        // terminal definition means anything. It began as IX_Users UNIQUE NONCLUSTERED over
        // EMAIL (01.00.00:L482-485, re-established after each rebuild at 01.00.05:L60-64 and
        // 01.00.06:L233-237), was dropped at 01.00.07:L76-77 and recreated over Username at
        // 01.00.07:L80-84, passed through a rename that is a no-op because both names are
        // identical (02.00.00:L129), and was dropped once more at 03.00.09:L420 before being
        // restated in its final form at 03.00.09:L422 as "ALTER TABLE Users ADD CONSTRAINT
        // IX_Users UNIQUE NONCLUSTERED (Username)". That is where its history ends, so the
        // terminal key-and-index set on this table is exactly PK_Users and IX_Users, beside the
        // three default constraints already recorded at their columns above.
        //
        // MIGRATION: uniqueness is asserted on Username and DELIBERATELY NOT on Email. The
        // legacy membership provider is registered so that a unique email address is NOT
        // required (Website/release.config:L244), and the back-fill at 03.00.13:L113-117
        // populated the replacement email column from the membership store with no uniqueness
        // test whatsoever, so duplicate addresses are legitimate existing data and asserting
        // uniqueness there would reject rows a live database already holds. Uniqueness on the
        // login name, by contrast, is load-bearing: it is the join key into the external
        // membership store and the value the sign-in path resolves a credential against.
        //
        // The physical name is preserved -- the empty object qualifier makes it simply
        // IX_Users. The schema expresses this as a unique CONSTRAINT, which SQL Server
        // implements as a unique index of that name; modelling it as a unique index keeps
        // UserId as the sole key, whereas modelling it as an alternate key would offer a second
        // target that a dependent could bind to by accident.
        builder.HasIndex(u => u.Username)
            .IsUnique()
            .HasDatabaseName("IX_Users");
    }
}
