using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

// MIGRATION: re-authored from Library/Components/Users/UserInfo.vb (class declared at line 41, 494
// lines) and Library/Components/Users/Membership/UserMembership.vb (class declared at line 41, 372
// lines). The two legacy classes are merged into this one aggregate root. UserInfo owned a
// UserMembership instance and a UserProfile instance and hydrated each of them on first read from
// inside a property getter - UserInfo.vb lines 196-204 call UserController.GetUserMembership and
// lines 237-245 call ProfileController.GetUserProfile - so simply reading a property performed
// database I/O. Rule T8 deletes that arrangement instead of translating it: this type is a plain
// data record, every getter is a field read, and the cross-store composition it used to hide is
// spelled out as an explicit obligation on the Infrastructure layer below.
//
// MIGRATION: this type carries no attribute of any kind. The legacy class implemented
// DotNetNuke.Services.Tokens.IPropertyAccess (UserInfo.vb line 42) and decorated its properties
// with Web-Forms property-editor and validation attributes imported from DotNetNuke.UI.WebControls
// - Browsable, SortOrder, Required, MaxLength, IsReadOnly and RegularExpressionValidator, at
// UserInfo.vb lines 87, 104, 121-123, 144, 161, 178, 195, 219, 236, 261, 284 and 301. Both trees
// are out of scope for this migration. The obligations they expressed do not vanish: the wire
// contract belongs to the DTOs at the API boundary and every length, required and format rule
// belongs to a FluentValidation validator in the Application layer. Do not reintroduce them here
// as DataAnnotations - the Domain project declares no package reference at all, by design.
//
// MIGRATION: the legacy null-sentinel system is not carried forward. Library/Components/Shared/
// Null.vb (lines 36-85) encodes absence as -1 for the integral types, 255 for a byte, MinValue for
// the floating-point, decimal and date types, Guid.Empty for a GUID and - the trap - the EMPTY
// STRING for text, and the legacy constructor seeded its fields from that table (UserInfo.vb lines
// 66-69 set the identifiers to Null.NullInteger). Per Rule T7 this type expresses absence with
// nullable CLR types and initialises nothing to a sentinel. Sentinel semantics are preserved only
// at the DTO and API boundary, where the wire contract is externally observable.

/// <summary>
/// A DotNetNuke user: the aggregate root for identity, credentials and portal membership.
/// </summary>
/// <remarks>
/// <para>
/// This aggregate spans two physically separate stores, and that split is the single most
/// important thing to understand about it.
/// </para>
/// <para>
/// The first store is the DotNetNuke <c>dbo.Users</c> table, which in its terminal form holds
/// exactly nine columns. Those nine are the only members of this type that an entity
/// configuration may map, and they are grouped together below under the banner comment
/// "PART A - TERMINAL dbo.Users COLUMNS".
/// </para>
/// <para>
/// The second store is the ASP.NET 2.0 membership schema - <c>dbo.aspnet_Users</c> and
/// <c>dbo.aspnet_Membership</c>, keyed by <c>Username</c> rather than by <c>UserID</c>. Those
/// tables are installed by Microsoft's ASP.NET SQL registration payload, not by DotNetNuke. The
/// evidence is direct: the 88-script DotNetNuke upgrade chain only ever ALTERs those objects -
/// <c>04.00.00.SqlDataProvider</c> line 31 is
/// <c>ALTER PROCEDURE dbo.aspnet_Membership_UpdateUser</c> and line 119 is
/// <c>ALTER PROCEDURE dbo.aspnet_Membership_UpdateUserInfo</c>, each grafting DotNetNuke's own
/// failed-attempt and lockout bookkeeping onto a stored procedure it did not write. Replaying
/// the whole chain against an empty database therefore cannot produce the terminal schema. This
/// entity consequently does not own, define, create or recreate any <c>aspnet_*</c> object; it
/// merely carries a read-only snapshot of the facts held there.
/// </para>
/// <para>
/// The nine-column shape is confirmed independently by the schema itself. The terminal view
/// definition at <c>04.00.04.SqlDataProvider</c> lines 771-787 reads
/// <c>SELECT U.UserId, UP.PortalId, U.Username, U.FirstName, U.LastName, U.DisplayName,
/// U.IsSuperUser, U.Email, U.AffiliateId, U.UpdatePassword, UP.Authorised FROM Users U LEFT OUTER
/// JOIN UserPortals UP ON U.UserId = UP.UserId</c> - exactly those nine columns from
/// <c>Users</c>, with the portal identifier and the authorisation flag arriving from
/// <c>UserPortals</c>. That is why this type declares no <c>PortalId</c> scalar and no
/// authorisation flag of its own: a user is one row shared by every portal it belongs to, and
/// per-portal facts live on the <c>UserPortal</c> join entity reachable through
/// <see cref="UserPortals"/>.
/// </para>
/// <para>
/// How the columns reached their terminal form, since the upgrade chain is destructive and only
/// the terminal state is meaningful: <c>01.00.00.SqlDataProvider</c> lines 97-111 create the
/// table with a plaintext <c>Password nvarchar(20)</c>, a postal address and a telephone number;
/// lines 282-283 of <c>01.00.02</c> drop <c>CreatedDate</c> and <c>LastLoginDate</c>;
/// <c>01.00.05</c> lines 14-29 and <c>01.00.06</c> lines 182-198 rebuild the table twice through
/// a <c>Tmp_Users</c> copy, the second rebuild introducing <c>Username</c>; and
/// <c>02.02.01.SqlDataProvider</c> lines 50-51 then drop <c>Street</c>, <c>City</c>,
/// <c>Region</c>, <c>PostalCode</c>, <c>Country</c>, <c>Password</c>, <c>Email</c>, <c>Unit</c>
/// and <c>Telephone</c> in a single statement, moving credentials to the external membership
/// store and profile data to the profile tables. Nothing after
/// <c>04.00.04.SqlDataProvider</c> alters the table again. None of those removed columns is
/// mapped here, and none may be added back: profile data belongs to <c>UserProfileValue</c>,
/// reachable through <see cref="UserProfileValues"/>.
/// </para>
/// <para>
/// Obligations this type places on the layers around it, none of which the Domain layer may
/// discharge itself:
/// </para>
/// <list type="number">
///   <item>
///   <c>UserConfiguration</c> in the Infrastructure layer maps the nine terminal columns to
///   <c>dbo.Users</c> and calls <c>Ignore(...)</c> for each of the eleven external and
///   contextual snapshot properties. The full list of required <c>Ignore</c> calls is restated
///   immediately above those properties so it cannot drift out of sight.
///   </item>
///   <item>
///   <c>UserRepository</c>, and the security services beside it, compose those eleven values
///   explicitly - from <c>aspnet_Users</c>, from <c>aspnet_Membership</c>, from the
///   <c>UserPortals</c> row, or from the target password store. Composition is a deliberate,
///   inspectable step in a repository method. There must be no lazy-loading getter, and no
///   getter on this type may reach a repository, a cache, a request context or a clock.
///   </item>
///   <item>
///   The Application layer owns validation and projection. Nothing on this type is validated
///   here, and no instance of this type crosses the API boundary.
///   </item>
/// </list>
/// </remarks>
public sealed class User : Entity<int>
{
    // =====================================================================================
    // PART A - TERMINAL dbo.Users COLUMNS (9)
    //
    // These nine properties, and only these nine, are mapped by UserConfiguration to
    // dbo.Users. Every type, nullability and default below was read off the terminal state of
    // the 88-script DDL chain, not off the baseline create script, because the chain is
    // destructive: the table is dropped and rebuilt twice and later has nine columns removed in
    // one statement. Each property records the script and line that settled its shape.
    // =====================================================================================

    /// <summary>
    /// Gets or sets the identifier of this user, unique across the whole installation.
    /// </summary>
    /// <value>
    /// The value of the <c>UserID</c> column: <c>int IDENTITY(1, 1) NOT NULL</c>, established at
    /// <c>01.00.00.SqlDataProvider</c> line 98 and preserved through both table rebuilds
    /// (<c>01.00.05.SqlDataProvider</c> line 16 and <c>01.00.06.SqlDataProvider</c> line 184).
    /// </value>
    /// <remarks>
    /// <para>
    /// The primary key. Declared <c>PK_Users PRIMARY KEY NONCLUSTERED</c> at
    /// <c>01.00.00.SqlDataProvider</c> lines 477-481 and promoted to <c>CLUSTERED</c> at
    /// <c>03.00.13.SqlDataProvider</c> lines 23-32.
    /// </para>
    /// <para>
    /// The seed is 1, which is worth stating explicitly because it is unusual in this schema:
    /// <c>dbo.Portals</c> seeds at -1 - and its shipped default row was inserted with an explicit
    /// <c>PortalID</c> of 0, so both values are real portal keys - while <c>dbo.Roles</c>,
    /// <c>dbo.Tabs</c> and <c>dbo.Modules</c> each seed at 0, so for those aggregates a
    /// "default-looking" value is a real persisted identity. Users are not affected - 0 is never a persisted
    /// <c>UserID</c> - but no code may rely on that as a general rule, and neither this type nor
    /// its base ever deduces from the value itself whether a row exists. The base takes that
    /// answer from a caller that holds it: <see cref="Entity{TId}.MarkIdentityPersisted"/> declares
    /// it and <see cref="Entity{TId}.IdentityIsPersisted"/> reads it back, which is also what decides
    /// whether two instances are compared by <see cref="UserId"/> or by object reference. Nothing
    /// declares it automatically, so an account materialised from the database compares by object
    /// reference until some caller declares it. Whether a row should
    /// be inserted or updated remains a question for the persistence layer's change tracker, which
    /// knows more than that flag does.
    /// </para>
    /// <para>
    /// The legacy field was seeded to <c>Null.NullInteger</c>, that is -1
    /// (<c>UserInfo.vb</c> line 66), and the legacy token accessor treated -1 as "anonymous"
    /// (<c>UserInfo.vb</c> line 430). Neither convention survives: this property is a plain
    /// non-nullable integer, and "no user" is expressed by the absence of a
    /// <see cref="User"/> instance.
    /// </para>
    /// </remarks>
    public int UserId { get; set; }

    /// <summary>
    /// Gets or sets the login name of this user.
    /// </summary>
    /// <value>
    /// The value of the <c>Username</c> column: <c>nvarchar(100) NOT NULL</c>, added by the
    /// second table rebuild at <c>01.00.06.SqlDataProvider</c> line 197.
    /// </value>
    /// <remarks>
    /// <para>
    /// Unique across the installation. The unique constraint originally covered <c>Email</c>
    /// (<c>01.00.00.SqlDataProvider</c> lines 482-485); <c>01.00.07.SqlDataProvider</c> lines
    /// 76-84 drop it and recreate it over <c>Username</c>, and
    /// <c>03.00.09.SqlDataProvider</c> lines 420-422 restate it in the templated form as
    /// <c>UNIQUE NONCLUSTERED ([Username])</c>. It is still a unique index and not the primary
    /// key, so <see cref="UserId"/> remains the identity.
    /// </para>
    /// <para>
    /// This is also the join key into the external membership store, which knows nothing of
    /// <see cref="UserId"/>: every cross-store statement in the legacy chain matches
    /// <c>Users.Username</c> against <c>aspnet_Users.UserName</c> - see
    /// <c>02.02.01.SqlDataProvider</c> lines 18-44 and the email back-fill at
    /// <c>03.00.13.SqlDataProvider</c> lines 113-117. Changing this value therefore rehomes the
    /// user's credentials, which is why the legacy editor marked the field read-only
    /// (<c>UserInfo.vb</c> line 301). That is a user-interface affordance, so it is expressed in
    /// the Angular form and in the update request DTO rather than by making this property
    /// immutable, which would obstruct materialisation.
    /// </para>
    /// <para>
    /// The values were originally back-filled from the email column
    /// (<c>01.00.06.SqlDataProvider</c> lines 268-270, <c>update Users set Username = Email</c>),
    /// so long-lived installations hold login names that look like email addresses and one -
    /// the built-in host account, identified at <c>01.00.02.SqlDataProvider</c> lines 246-251 by
    /// <c>where Email = 'host'</c> - that does not. No format rule of any kind may be imposed
    /// here.
    /// </para>
    /// <para>
    /// The legacy setter also wrote a duplicate copy onto the membership object
    /// (<c>UserInfo.vb</c> lines 305-311, targeting the deprecated member at
    /// <c>UserMembership.vb</c> lines 356-366). The duplicate is collapsed into this single
    /// property.
    /// </para>
    /// </remarks>
    public string Username { get; set; }

    /// <summary>
    /// Gets or sets the given name of this user.
    /// </summary>
    /// <value>
    /// The value of the <c>FirstName</c> column: <c>nvarchar(50) NOT NULL</c>, present since
    /// <c>01.00.00.SqlDataProvider</c> line 99 and carried through both rebuilds
    /// (<c>01.00.06.SqlDataProvider</c> line 185).
    /// </value>
    /// <remarks>
    /// A genuine column on <c>dbo.Users</c>, not profile data. The terminal view at
    /// <c>04.00.04.SqlDataProvider</c> line 777 selects it directly from the table, and the
    /// legacy data reader read it straight off that row
    /// (<c>AspNetMembershipProvider.vb</c> line 333). The legacy property nevertheless delegated
    /// to the lazily hydrated profile wrapper (<c>UserInfo.vb</c> lines 144-151), so reading a
    /// first name could trigger a profile fetch for a value that was already in hand. This type
    /// models the table, not the wrapper.
    /// </remarks>
    public string FirstName { get; set; }

    /// <summary>
    /// Gets or sets the family name of this user.
    /// </summary>
    /// <value>
    /// The value of the <c>LastName</c> column: <c>nvarchar(50) NOT NULL</c>.
    /// </value>
    /// <remarks>
    /// Nullable in the original create script (<c>01.00.00.SqlDataProvider</c> line 100) and
    /// made <c>NOT NULL</c> by the first table rebuild
    /// (<c>01.00.05.SqlDataProvider</c> line 18), which is precisely why the terminal state has
    /// to be read from the end of the chain rather than from the baseline. As with
    /// <see cref="FirstName"/>, this is a real column - <c>04.00.04.SqlDataProvider</c> line 778
    /// and <c>AspNetMembershipProvider.vb</c> line 334 - even though the legacy property
    /// delegated to the profile wrapper (<c>UserInfo.vb</c> lines 178-185).
    /// </remarks>
    public string LastName { get; set; }

    /// <summary>
    /// Gets or sets the name shown for this user in the user interface.
    /// </summary>
    /// <value>
    /// The value of the <c>DisplayName</c> column: <c>nvarchar(128) NOT NULL</c> with a database
    /// default of the empty string, added at <c>03.02.03.SqlDataProvider</c> lines 628-632 as
    /// <c>DisplayName nvarchar(128) NOT NULL CONSTRAINT DF_Users_DisplayName DEFAULT ''</c> and
    /// added again, guarded by a version check, at <c>04.00.04.SqlDataProvider</c> lines
    /// 666-673 for installations that skipped the earlier script.
    /// </value>
    /// <remarks>
    /// <para>
    /// The database default is the empty string, not <see langword="null"/>, so an unset display
    /// name is legitimately empty text. That is not a null sentinel to be normalised away: the
    /// column is <c>NOT NULL</c>, the empty string is the value the database actually stores,
    /// and the property is correspondingly non-nullable. Choosing a fallback for presentation is
    /// the Application layer's decision, taken at projection time.
    /// </para>
    /// <para>
    /// The legacy model also offered a token-substitution helper that composed this value from a
    /// format string containing <c>[USERID]</c>, <c>[FIRSTNAME]</c>, <c>[LASTNAME]</c> and
    /// <c>[USERNAME]</c> placeholders (<c>UserInfo.vb</c> lines 358-368), and a deprecated
    /// full-name property that fell back to first and last name joined by a space
    /// (<c>UserInfo.vb</c> lines 374-386). Neither is reproduced. Composition and formatting are
    /// behaviour, and behaviour belongs in an Application service; the token-replacement
    /// subsystem those placeholders belong to is out of scope entirely.
    /// </para>
    /// </remarks>
    public string DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the email address recorded for this user, which may be absent.
    /// </summary>
    /// <value>
    /// The value of the <c>Email</c> column: <c>nvarchar(256) NULL</c>, re-added at
    /// <c>03.00.13.SqlDataProvider</c> lines 109-110.
    /// </value>
    /// <remarks>
    /// <para>
    /// This column has been added, dropped and re-added. It started as
    /// <c>nvarchar(100) NOT NULL</c> with a unique constraint over it
    /// (<c>01.00.00.SqlDataProvider</c> line 107, with the constraint at lines 482-485); the
    /// unique constraint moved to <see cref="Username"/> at
    /// <c>01.00.07.SqlDataProvider</c> lines 76-84; the column itself
    /// was dropped when credentials moved to the external membership store
    /// (<c>02.02.01.SqlDataProvider</c> lines 50-51); and it returned as a nullable,
    /// wider, denormalised copy at <c>03.00.13.SqlDataProvider</c> lines 109-110, back-filled
    /// <c>FROM dbo.aspnet_Membership INNER JOIN dbo.aspnet_Users</c> at lines 113-117 of the
    /// same script. Terminal state: nullable, 256 characters, not unique - and the authoritative
    /// copy still lives in <c>aspnet_Membership.Email</c>, so writes must keep the two stores in
    /// step.
    /// </para>
    /// <para>
    /// The type is deliberately a plain nullable string and not the <c>EmailAddress</c> value
    /// object this layer also defines. Two measured facts force that. The column is nullable, so
    /// absence must be representable; and the data is not guaranteed to be an email address at
    /// all - the built-in host account is
    /// identified in the upgrade chain by <c>where Email = 'host'</c>
    /// (<c>01.00.02.SqlDataProvider</c> lines 246-251), and a value object that rejected
    /// <c>host</c> would make that shipped row impossible to materialise. A domain model that
    /// cannot load the data in the database is not a stricter model, it is a broken one.
    /// </para>
    /// <para>
    /// Format validation therefore lives at the Application boundary, where a request can be
    /// rejected without preventing an existing row from loading. The legacy rule to reproduce
    /// there is the regular-expression validator attached to the legacy property
    /// (<c>UserInfo.vb</c> lines 121-123), together with the provider setting
    /// <c>requiresUniqueEmail="false"</c> at <c>Website/release.config</c> line 244 - email
    /// uniqueness is not enforced by this installation and must not be introduced as a new
    /// constraint during a migration.
    /// </para>
    /// <para>
    /// As with <see cref="Username"/>, the legacy setter maintained a duplicate copy on the
    /// membership object (<c>UserInfo.vb</c> lines 127-133, targeting the deprecated member at
    /// <c>UserMembership.vb</c> lines 344-354). The duplicate is collapsed into this single
    /// property.
    /// </para>
    /// </remarks>
    public string? Email { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this user is a host-level super user.
    /// </summary>
    /// <value>
    /// The value of the <c>IsSuperUser</c> column: <c>bit NOT NULL</c> with a database default of
    /// <c>0</c>.
    /// </value>
    /// <remarks>
    /// <para>
    /// Added at <c>01.00.02.SqlDataProvider</c> lines 242-243 as
    /// <c>IsSuperUser bit NOT NULL CONSTRAINT DF_Users_IsSuperUser DEFAULT (0)</c>, and
    /// re-asserted after the templating rename at <c>03.01.01.SqlDataProvider</c> lines
    /// 1342-1353, which drops whatever default constraint it finds, restates the column as
    /// <c>[bit] NOT NULL</c> and re-adds the zero default.
    /// </para>
    /// <para>
    /// This flag selects which store answers a question, which makes it far more than a
    /// convenience. A super user is not scoped to a portal, so its approval state is held in
    /// <c>aspnet_Membership</c>, while an ordinary user's is held on its <c>UserPortals</c> row;
    /// the legacy provider branches on exactly this flag in both directions
    /// (<c>AspNetMembershipProvider.vb</c> lines 355-361 and 381-384 for the ordinary case,
    /// lines 413-416 for the super-user case). It also decides which portal the credential check
    /// runs against (lines 1483-1493) and it is the escape hatch in the legacy role test
    /// (<c>UserInfo.vb</c> line 330). Anything reading <see cref="IsApproved"/> must know which
    /// branch produced it.
    /// </para>
    /// <para>
    /// The property is a plain non-nullable boolean. The legacy field was seeded from
    /// <c>Null.NullBoolean</c> (<c>UserInfo.vb</c> line 68), which is simply
    /// <see langword="false"/> (<c>Null.vb</c> lines 76-80) - a sentinel indistinguishable from
    /// a real value, and therefore no sentinel at all. Nothing is lost by dropping it.
    /// </para>
    /// </remarks>
    public bool IsSuperUser { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the affiliate that referred this user, if any.
    /// </summary>
    /// <value>
    /// The value of the <c>AffiliateID</c> column: <c>int NULL</c>, added at
    /// <c>02.00.00.SqlDataProvider</c> lines 6957-6958.
    /// </value>
    /// <remarks>
    /// <para>
    /// Genuinely optional - most users are not referred - and mapped as
    /// <see cref="System.Nullable{T}"/> of <see cref="int"/> so that absence is expressed by
    /// <see langword="null"/> rather than by a magic number. The legacy model could not do that:
    /// the field was a non-nullable integer seeded to <c>Null.NullInteger</c>, that is -1
    /// (<c>UserInfo.vb</c> line 69), and the reader converted a database <c>NULL</c> back into
    /// -1 through the sentinel helper (<c>AspNetMembershipProvider.vb</c> line 340). Reading -1
    /// here as "no affiliate" would be a bug: this property is <see langword="null"/> when there
    /// is no affiliate, and -1 if some row genuinely holds -1.
    /// </para>
    /// <para>
    /// Note the casing difference. The property is <c>AffiliateId</c>, following .NET naming;
    /// the column is <c>AffiliateID</c>. The entity configuration must state the column name
    /// explicitly rather than rely on convention - the schema is immutable for this migration.
    /// </para>
    /// <para>
    /// The affiliate and vendor administration features are out of scope, so this value is
    /// carried and persisted but never interpreted here. That is deliberate: dropping it would
    /// silently discard referral data on every update.
    /// </para>
    /// </remarks>
    public int? AffiliateId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this user must change password at next sign-in.
    /// </summary>
    /// <value>
    /// The value of the <c>UpdatePassword</c> column: <c>bit NOT NULL</c> with a database default
    /// of <c>0</c>.
    /// </value>
    /// <remarks>
    /// <para>
    /// Added alongside <see cref="DisplayName"/> at <c>03.02.03.SqlDataProvider</c> lines
    /// 628-632 as
    /// <c>UpdatePassword bit NOT NULL CONSTRAINT DF_Users_UpdatePassword DEFAULT 0</c>, and
    /// again under a version guard at <c>04.00.04.SqlDataProvider</c> lines 666-673.
    /// </para>
    /// <para>
    /// This is the one credential-related fact that belongs to <c>dbo.Users</c> rather than to
    /// the external membership store, which is why it sits here among the mapped columns while
    /// everything else about passwords sits in the snapshot section below. The legacy model
    /// obscured that: the flag was declared on the membership wrapper
    /// (<c>UserMembership.vb</c> lines 323-330) yet read from and written to the DotNetNuke user
    /// row (<c>AspNetMembershipProvider.vb</c> lines 352 and 380, and the persisting call at
    /// line 1369). Tellingly, its setter is the only one in that wrapper that does not touch the
    /// hydration flag - the legacy author knew it was not membership data.
    /// </para>
    /// <para>
    /// Enforcement is a sign-in concern and lives in the Application layer's authentication
    /// service; this type only records the requirement.
    /// </para>
    /// </remarks>
    public bool UpdatePassword { get; set; }

    // =====================================================================================
    // IDENTITY (equality only - not a mapped column)
    // =====================================================================================

    /// <inheritdoc />
    /// <remarks>
    /// Forwards <see cref="UserId"/> so that <see cref="Entity{TId}"/> can compare two users by
    /// identity. This member exists for equality alone: it is get-only, it carries no attribute,
    /// and no entity configuration binds it to a column. <c>UserConfiguration</c> maps
    /// <see cref="UserId"/> and names that property in its explicit key declaration instead.
    /// </remarks>
    public override int Identity => UserId;

    // =====================================================================================
    // PART B - EXTERNAL AND CONTEXTUAL MEMBERSHIP SNAPSHOT (11)
    //
    // NOT COLUMNS OF dbo.Users. Every property in this section is a snapshot of state held
    // somewhere else: in dbo.aspnet_Users, in dbo.aspnet_Membership, on the UserPortals row, or
    // in the target password store. They are grouped here, apart from the nine mapped columns
    // above, so the boundary between "persisted by this entity" and "composed for this entity"
    // is impossible to misread.
    //
    // MIGRATION: the invariant, and it admits no partial satisfaction - UserConfiguration excludes
    // every property in this section from the model, and the executable list of exclusions lives
    // there rather than being copied here, where a second copy could disagree with the first.
    // Adding a property to this section without excluding it there makes the object-relational
    // mapper infer a column on dbo.Users that does not exist, and every query against the table
    // then fails at run time - a failure the compiler cannot catch, which is why the boundary is
    // marked this emphatically on both sides.
    //
    // MIGRATION: UserRepository, and the security services beside it, MUST compose these values
    // explicitly - one deliberate, inspectable step inside a repository or service method,
    // exactly as the legacy provider did in AspNetMembershipProvider.vb lines 310-419 and 1128-
    // 1141. There must be NO lazy-loading getter. The legacy design hydrated on first read from
    // inside a getter (UserInfo.vb lines 196-204), which meant a property access could open a
    // database connection, could not be reasoned about, and could not be tested; Rule T8 deletes
    // that mechanism rather than porting it. Where a caller needs a user without this snapshot,
    // the repository returns one with these properties left null - a null here means "not
    // composed on this read path", which is exactly the distinction the legacy boolean hydration
    // flag was trying and failing to express.
    //
    // MIGRATION: the legacy ObjectHydrated bookkeeping is intentionally absent. It was declared
    // at UserMembership.vb lines 243-253 and flipped by nearly every setter in that file (lines
    // 89-91, 109-111, 129-131, 149-151, 169-171, 189-191, 209-211, 229-231, 269-271, 289-291 and
    // 309-311), and its own setter contained the self-referential guard
    // "If Not ObjectHydrated Then _ObjectHydrated = True". It existed solely to stop the
    // progressive-hydration getters from re-fetching. With no progressive hydration there is
    // nothing to bookkeep, so per Rule T8 the workaround produces no target member.
    // =====================================================================================

    /// <summary>
    /// Gets or sets the composed approval state of this user, or <see langword="null"/> when it
    /// was not composed on the read path that produced this instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CONTEXT-SENSITIVE. There is no single authoritative <c>dbo.Users</c> column behind this
    /// property, and treating it as though there were is the most likely way to get authorisation
    /// wrong. Which store answers depends on <see cref="IsSuperUser"/>:
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///   For an ordinary portal user, approval is per-portal authorisation and originates from
    ///   the <c>UserPortals.Authorised</c> column - British spelling in the schema - added as
    ///   <c>Authorised bit NOT NULL CONSTRAINT DF_UserPortals_Authorised DEFAULT 1</c> at
    ///   <c>03.02.03.SqlDataProvider</c> lines 637-641 and surfaced through the terminal view at
    ///   <c>04.00.04.SqlDataProvider</c> line 784. The legacy provider reads it as
    ///   <c>isApproved = Convert.ToBoolean(dr("Authorised"))</c> guarded by
    ///   <c>If Not objUserInfo.IsSuperUser</c> (<c>AspNetMembershipProvider.vb</c> lines
    ///   355-361) and assigns it under the same guard at lines 381-384. In the target model that
    ///   fact belongs to the <c>UserPortal</c> join entity - carried there as its authorisation
    ///   flag, <c>UserPortal.IsAuthorised</c> - reachable through <see cref="UserPortals"/>, and
    ///   it is the value the authorisation policy must consult for a given portal.
    ///   </item>
    ///   <item>
    ///   For a super user there is no portal to be authorised against, so approval originates
    ///   from <c>aspnet_Membership.IsApproved</c> - declared <c>bit NOT NULL</c> at
    ///   <c>InstallMembership.sql</c> line 90. The legacy provider states it exactly that way:
    ///   <c>If user.IsSuperUser Then user.Membership.Approved = aspNetUser.IsApproved</c>, above
    ///   the comment "For superusers the Approved info is stored in aspnet membership"
    ///   (<c>AspNetMembershipProvider.vb</c> lines 413-416).
    ///   </item>
    /// </list>
    /// <para>
    /// Because a user may belong to several portals, a single boolean on the aggregate cannot
    /// express the ordinary-user case faithfully - it can only carry the answer for the one
    /// portal the read was scoped to. Treat it as a hydrated snapshot and never as the
    /// authoritative record. The authoritative per-portal value is on the <c>UserPortal</c> row;
    /// the authoritative super-user value is in the external membership store.
    /// </para>
    /// <para>
    /// The legacy field defaulted to <see langword="true"/> (<c>UserMembership.vb</c> line 45).
    /// That default is deliberately not reproduced: an uncomposed value here is
    /// <see langword="null"/>, so a caller that forgets to compose it cannot silently obtain
    /// "approved". Failing closed is the only safe behaviour for an authorisation input.
    /// </para>
    /// <para>
    /// The approval workflow itself is not modelled here. The legacy sign-in path could approve a
    /// user in flight when a verification code matched <c>portalId &amp; "-" &amp; userId</c> and
    /// persist that immediately (<c>AspNetMembershipProvider.vb</c> lines 1465-1477); the same
    /// concatenation was also exposed as a token (<c>UserInfo.vb</c> lines 442-444). Neither the
    /// workflow nor a verification-code property appears on this type - the workflow belongs to
    /// the Application layer's authentication service, and the token subsystem is out of scope.
    /// </para>
    /// </remarks>
    public bool? IsApproved { get; set; }

    /// <summary>
    /// Gets or sets the composed creation timestamp of this user's membership record, or
    /// <see langword="null"/> when it was not composed on this read path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Composed from <c>aspnet_Membership.CreateDate</c>, declared <c>datetime NOT NULL</c> at
    /// <c>InstallMembership.sql</c> line 92. The legacy provider assigns it from the membership
    /// user's creation date at <c>AspNetMembershipProvider.vb</c> line 405.
    /// </para>
    /// <para>
    /// This is external membership data and NOT a common audit field, which is why this type
    /// derives from <see cref="Entity{TId}"/> and deliberately not from an auditable base class.
    /// Two facts make that unavoidable. The value lives in another store, on a table this
    /// migration does not own, so a shared audit base would promise a column that
    /// <c>dbo.Users</c> does not have. And <c>dbo.Users</c> genuinely did carry a
    /// <c>CreatedDate</c> column once - <c>01.00.00.SqlDataProvider</c> line 108 - which
    /// <c>01.00.02.SqlDataProvider</c> lines 282-283 dropped after copying the values onto
    /// <c>UserPortals</c> at lines 254-280 of the same script, and which
    /// <c>02.02.01.SqlDataProvider</c> lines 54-55 then dropped from <c>UserPortals</c> as well
    /// once the external store became authoritative. Modelling it as an audit column would
    /// resurrect a column the schema removed twice.
    /// </para>
    /// <para>
    /// The legacy chain even reconciled the two stores explicitly, updating
    /// <c>aspnet_Membership.CreateDate</c> from <c>UserPortals.CreatedDate</c> during the upgrade
    /// (<c>02.02.01.SqlDataProvider</c> lines 18-24). The external store won, and it still owns
    /// the value.
    /// </para>
    /// </remarks>
    public DateTime? CreatedDate { get; set; }

    /// <summary>
    /// Gets or sets the composed online state of this user at the moment the query ran, or
    /// <see langword="null"/> when it was not composed on this read path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// QUERY-TIME SNAPSHOT. This value is derived outside the entity, by comparing
    /// <see cref="LastActivityDate"/> against a configured online window, and it is stale the
    /// instant it is written. It is data, nothing more.
    /// </para>
    /// <para>
    /// This property must never read the current time, must never have a clock injected beside
    /// it, and must never perform any I/O - Rule T6. The Domain layer offers a clock abstraction
    /// precisely so that time-dependent decisions are made in a testable service; a record does
    /// not consult one. Anything computing online-ness needs three inputs it cannot obtain from
    /// this type: the current instant, the configured window, and the activity timestamp. Only
    /// the third is here.
    /// </para>
    /// <para>
    /// The derivation to reproduce is already explicit in the schema. The membership installer's
    /// online-count procedure computes
    /// <c>DATEADD(minute, -(@MinutesSinceLastInActive), @CurrentTimeUtc)</c> and selects rows
    /// whose <c>LastActivityDate</c> exceeds it (<c>InstallMembership.sql</c> lines 1264-1283) -
    /// note that both the window and the current instant are procedure PARAMETERS, passed in from
    /// outside. The window for this installation is fifteen minutes, configured as
    /// <c>userIsOnlineTimeWindow="15"</c> at <c>Website/release.config</c> line 219, and it
    /// belongs in a bound options class in the target.
    /// </para>
    /// <para>
    /// The legacy code confirms the value was always assigned from the outside rather than
    /// computed by the record: <c>user.Membership.IsOnLine = IsUserOnline(user)</c> at
    /// <c>AspNetMembershipProvider.vb</c> line 1139, where the helper at lines 1267-1290 consults
    /// a cache and then the database. Both are I/O paths, and both stay in Infrastructure. The
    /// users-online tracking subsystem and its purge job are out of scope for this migration, so
    /// a repository that cannot answer the question leaves this property
    /// <see langword="null"/> rather than guessing <see langword="false"/>.
    /// </para>
    /// <para>
    /// Spelling note: the legacy member was <c>IsOnLine</c>, with a capital L
    /// (<c>UserMembership.vb</c> lines 123-133). Renamed to <c>IsOnline</c> here for idiomatic
    /// .NET casing. Nothing maps this property to a column, so the rename has no schema
    /// consequence.
    /// </para>
    /// </remarks>
    public bool? IsOnline { get; set; }

    /// <summary>
    /// Gets or sets the composed timestamp of this user's last recorded activity, or
    /// <see langword="null"/> when it was not composed on this read path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Composed from <c>aspnet_Users.LastActivityDate</c> - note the parent membership table,
    /// <c>aspnet_Users</c>, not <c>aspnet_Membership</c>: the online-count procedure at
    /// <c>InstallMembership.sql</c> lines 1274-1281 reads it from the <c>aspnet_Users</c> alias.
    /// The legacy provider assigns it at <c>AspNetMembershipProvider.vb</c> line 406 and stamps
    /// it with the current instant when persisting a membership update at line 519.
    /// </para>
    /// <para>
    /// This is the input from which <see cref="IsOnline"/> is derived. Keeping the raw timestamp
    /// alongside the derived flag is deliberate: a caller that knows the current time and the
    /// configured window can recompute online-ness for itself, which the boolean alone would not
    /// permit. Stamping this value on activity is a repository or middleware concern, never a
    /// side effect of a property setter.
    /// </para>
    /// </remarks>
    public DateTime? LastActivityDate { get; set; }

    /// <summary>
    /// Gets or sets the composed timestamp of this user's most recent lockout, or
    /// <see langword="null"/> when it was not composed on this read path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Composed from <c>aspnet_Membership.LastLockoutDate</c>, declared
    /// <c>datetime NOT NULL</c> at <c>InstallMembership.sql</c> line 95 and assigned by the
    /// legacy provider at <c>AspNetMembershipProvider.vb</c> line 407.
    /// </para>
    /// <para>
    /// It pairs with <see cref="IsLockedOut"/> to drive automatic unlocking, and that logic is
    /// emphatically not here. The legacy helper reads a host setting for the unlock duration,
    /// falls back to ten minutes when the setting is absent, treats zero as "never auto-unlock",
    /// and compares <c>aspNetUser.LastLockoutDate &lt; Date.Now.AddMinutes(-1 * intTimeout)</c>
    /// before unlocking in the data store (<c>AspNetMembershipProvider.vb</c> lines 65-87). That
    /// is ambient time plus configuration plus a write - three things a domain record must not
    /// do. In the target the same decision belongs to an Infrastructure security service using
    /// the injected clock and bound options; this property only carries the timestamp it needs.
    /// </para>
    /// </remarks>
    public DateTime? LastLockoutDate { get; set; }

    /// <summary>
    /// Gets or sets the composed timestamp of this user's most recent sign-in, or
    /// <see langword="null"/> when it was not composed on this read path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Composed from <c>aspnet_Membership.LastLoginDate</c>, declared
    /// <c>datetime NOT NULL</c> at <c>InstallMembership.sql</c> line 93 and assigned by the
    /// legacy provider at <c>AspNetMembershipProvider.vb</c> line 408.
    /// </para>
    /// <para>
    /// Like <see cref="CreatedDate"/>, this value has migrated between stores: <c>dbo.Users</c>
    /// held a <c>LastLoginDate</c> column at <c>01.00.00.SqlDataProvider</c> line 109, which was
    /// copied to <c>UserPortals</c> and dropped at <c>01.00.02.SqlDataProvider</c> lines 254-283,
    /// then dropped from <c>UserPortals</c> too at <c>02.02.01.SqlDataProvider</c> lines 54-55
    /// after the upgrade reconciled it into <c>aspnet_Membership</c> at lines 28-34 of that same
    /// script. The external store is authoritative, so this is a snapshot rather than a mapped
    /// column.
    /// </para>
    /// </remarks>
    public DateTime? LastLoginDate { get; set; }

    /// <summary>
    /// Gets or sets the composed timestamp of this user's most recent password change, or
    /// <see langword="null"/> when it was not composed on this read path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Composed from <c>aspnet_Membership.LastPasswordChangedDate</c>, declared
    /// <c>datetime NOT NULL</c> at <c>InstallMembership.sql</c> line 94 and assigned by the
    /// legacy provider at <c>AspNetMembershipProvider.vb</c> line 409. Note the name difference
    /// preserved from the legacy model: the column says <c>Changed</c> and the property says
    /// <c>Change</c> (<c>UserMembership.vb</c> lines 203-213); nothing maps this property to a
    /// column, so no name is broken by keeping the model-side spelling.
    /// </para>
    /// <para>
    /// In the target this is the natural place to observe that a stored credential predates the
    /// migration, which matters because such an account can only be recovered by the administrative
    /// reset described on <see cref="PasswordHash"/>. It
    /// pairs with <see cref="UpdatePassword"/>, the one credential fact that really is a
    /// <c>dbo.Users</c> column, to drive password-age policy in the Application layer. No policy
    /// is evaluated here.
    /// </para>
    /// </remarks>
    public DateTime? LastPasswordChangeDate { get; set; }

    /// <summary>
    /// Gets or sets the composed lockout state of this user, or <see langword="null"/> when it was
    /// not composed on this read path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Composed from <c>aspnet_Membership.IsLockedOut</c>, declared <c>bit NOT NULL</c> at
    /// <c>InstallMembership.sql</c> line 91. The legacy provider assigns it at
    /// <c>AspNetMembershipProvider.vb</c> line 410 and clears it in memory when an automatic
    /// unlock succeeds at line 1457.
    /// </para>
    /// <para>
    /// Lockout is produced by the failed-attempt counters that live entirely in the external
    /// store - <c>FailedPasswordAttemptCount</c>,
    /// <c>FailedPasswordAttemptWindowStart</c>, <c>FailedPasswordAnswerAttemptCount</c> and
    /// <c>FailedPasswordAnswerAttemptWindowStart</c> at <c>InstallMembership.sql</c> lines 96-99,
    /// maintained by the membership procedures that the DotNetNuke chain ALTERs rather than
    /// creates (<c>04.00.00.SqlDataProvider</c> lines 31 and 119). None of those counters is
    /// modelled on this type: they are not <c>dbo.Users</c> columns and no in-scope requirement
    /// reads them individually. Should the target need its own lockout bookkeeping it belongs in
    /// the Infrastructure security services, not here.
    /// </para>
    /// <para>
    /// The legacy member was named <c>LockedOut</c> (<c>UserMembership.vb</c> lines 223-233);
    /// renamed to <c>IsLockedOut</c> to match the external column and .NET boolean naming.
    /// Uncomposed is <see langword="null"/>, never <see langword="false"/>, so a sign-in path
    /// that forgets to compose it cannot conclude "not locked out".
    /// </para>
    /// </remarks>
    public bool? IsLockedOut { get; set; }

    /// <summary>
    /// Gets or sets the one-way hash of this user's password, or <see langword="null"/> when it
    /// was not composed on this read path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This property is the deliberate replacement for the legacy reversible password concept,
    /// and the change is a security fix rather than a refactoring. It is the single most
    /// significant behavioural divergence in the user domain and is recorded as such in the
    /// repository-root <c>MIGRATION_NOTES.md</c>.
    /// </para>
    /// <para>
    /// What the legacy installation actually did, measured rather than assumed. The membership
    /// provider is registered for reversible storage with credential retrieval enabled, so stored
    /// credentials are recoverable ciphertext rather than hashes, and the symmetric key that
    /// recovers them was itself committed to source control. Neither that key nor its location is
    /// reproduced here, and neither must ever be copied into source, a log, a test fixture or a
    /// configuration template. Ciphertext plus a committed key is plaintext, and the provider
    /// stack exercised that capability directly - the legacy code calls
    /// <c>objPortalSecurity.Decrypt(EncryptionKey, objUser.Membership.Password)</c> at
    /// <c>AspNetMembershipProvider.vb</c> line 1720 and exposes a password-returning operation at
    /// lines 1025-1038. The original 1.0.0 schema was blunter still: <c>dbo.Users</c> carried
    /// <c>[Password] [nvarchar] (20) NOT NULL</c> in clear text
    /// (<c>01.00.00.SqlDataProvider</c> line 106) until <c>02.02.01.SqlDataProvider</c> lines
    /// 50-51 dropped it.
    /// </para>
    /// <para>
    /// What the target does. Credentials are stored as a one-way BCrypt hash produced by the
    /// Infrastructure password hasher behind the Domain's hashing abstraction. The salt and cost
    /// factor are carried inside the hash string itself, so no separate salt property exists here.
    /// The external membership store's <c>PasswordSalt</c> and <c>PasswordFormat</c> columns
    /// (<c>InstallMembership.sql</c> lines 83-84) are read through the repository contract during
    /// migration and never become properties on this account entity. No encryption helper, key
    /// material or reversible-encryption path appears anywhere on this type.
    /// </para>
    /// <para>
    /// Migrating existing credentials. The opt-in <c>ILegacyCredentialVerifier</c> performs a
    /// bounded comparison against the external membership row during the migration window and
    /// returns only a boolean outcome. AuthService immediately replaces an accepted legacy value
    /// with BCrypt; it never exposes decrypted plaintext or a retrieval operation. Administrative
    /// reset remains the fallback for rows that cannot be verified or are not presented before the
    /// migration window closes.
    /// </para>
    /// <para>
    /// A value already produced by BCrypt is also re-hashed at the current cost once that cost has
    /// been raised. That work-factor upgrade and the legacy-format replacement share the same final
    /// repository write, but their verification paths remain deliberately separate.
    /// </para>
    /// <para>
    /// Password RETRIEVAL is deliberately not carried forward, in any form. There is no member
    /// here that returns a password, no operation that decrypts one, and no path by which this
    /// value may be projected into a response DTO, written to a log, or included in an error
    /// message. The legacy policy that IS preserved verbatim is the strength policy, because
    /// tightening it during a migration would lock out existing users: minimum length seven,
    /// zero required non-alphanumeric characters, no question-and-answer requirement
    /// (<c>Website/release.config</c> lines 241-243). It is enforced by a FluentValidation
    /// validator in the Application layer, not here.
    /// </para>
    /// </remarks>
    public string? PasswordHash { get; set; }

    /// <summary>
    /// Gets or sets the legacy password answer held in the external membership store, or
    /// <see langword="null"/> when it was not composed on this read path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SENSITIVE. Retained only so that legacy external-store data is representable during
    /// migration. It corresponds to <c>aspnet_Membership.PasswordAnswer</c>,
    /// <c>nvarchar(128)</c> at <c>InstallMembership.sql</c> line 89, and it is a credential in
    /// its own right: the legacy provider accepts it as the sole proof of identity for password
    /// recovery and reset (<c>AspNetMembershipProvider.vb</c> lines 1025-1038 and 1305-1313).
    /// </para>
    /// <para>
    /// This value must never be returned in an API DTO, never be written to a log, an audit
    /// entry, a trace or an exception message, and never be used by the new authentication path.
    /// Password recovery is not carried forward at all - see <see cref="PasswordHash"/> - so
    /// nothing in the target has a legitimate reason to read it.
    /// </para>
    /// <para>
    /// The installation does not even require it: the provider is registered with
    /// <c>requiresQuestionAndAnswer="false"</c> at <c>Website/release.config</c> line 241. It is
    /// also effectively write-only in the legacy model - the composition routine at
    /// <c>AspNetMembershipProvider.vb</c> lines 402-419 copies the question back but pointedly
    /// never the answer - so the ordinary state of this property, even against a live legacy
    /// database, is <see langword="null"/>.
    /// </para>
    /// </remarks>
    public string? PasswordAnswer { get; set; }

    /// <summary>
    /// Gets or sets the legacy password question held in the external membership store, or
    /// <see langword="null"/> when it was not composed on this read path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SENSITIVE. Retained on the same terms as <see cref="PasswordAnswer"/>: legacy
    /// external-store data only. It corresponds to <c>aspnet_Membership.PasswordQuestion</c>,
    /// <c>nvarchar(256)</c> at <c>InstallMembership.sql</c> line 88, and the legacy provider
    /// composes it at <c>AspNetMembershipProvider.vb</c> line 411 and lets it be changed together
    /// with the answer at lines 813-824.
    /// </para>
    /// <para>
    /// It must never be returned in an API DTO, never be written to a log, and never be used by
    /// the new authentication path. A question is not secret on its own, but publishing it
    /// narrows the search for the answer, and the answer is a credential; the two are therefore
    /// held to one rule. The provider setting <c>requiresQuestionAndAnswer="false"</c>
    /// (<c>Website/release.config</c> line 241) means no in-scope workflow depends on it.
    /// </para>
    /// </remarks>
    public string? PasswordQuestion { get; set; }

    // =====================================================================================
    // NAVIGATIONS (5)
    //
    // Typed, initialised collections replacing the legacy wrapper objects and the untyped
    // pre-generics collections. Each is get-only with a populated backing instance, so a caller
    // may add to it and a materialiser may fill it, but nobody can swap the instance out from
    // under a change tracker or leave it null for a caller to dereference.
    //
    // MIGRATION: no scalar PortalId appears anywhere on this type. A user is one row in
    // dbo.Users shared by every portal it belongs to; the per-portal facts - membership,
    // authorisation, creation and last sign-in - live on the UserPortals row. The terminal view
    // at 04.00.04.SqlDataProvider lines 771-787 shows exactly that, taking PortalId and
    // Authorised from a LEFT OUTER JOIN to UserPortals rather than from Users. The legacy model
    // did carry a PortalID field (UserInfo.vb lines 219-226), seeded to the -1 sentinel and set
    // per-read by the provider (AspNetMembershipProvider.vb line 330), which made a user object
    // silently portal-specific and encouraged authorisation checks against ambient state. The
    // target replaces it with the scoped portal context resolved once per request, and with the
    // UserPortal navigation below.
    //
    // MIGRATION: the legacy Roles string array (UserInfo.vb lines 261-274) and the role test
    // built on it (lines 328-347) are gone. That getter constructed a RoleController and called
    // GetRolesByUser on first read - I/O from a property - and the test then compared strings,
    // special-casing super users, an "all users" pseudo-role and a "[userId]" bracket form.
    // Role data now arrives through the UserRoles navigation, and authorisation is decided by
    // policy-based authorisation handlers in the API layer against claims. No IsInRole method
    // exists here.
    // =====================================================================================

    /// <summary>
    /// Gets this user's per-portal membership rows.
    /// </summary>
    /// <remarks>
    /// The join to <c>dbo.UserPortals</c>, and the only correct home for per-portal facts: the
    /// portal identifier, the <c>Authorised</c> flag behind <see cref="IsApproved"/> for ordinary
    /// users, and the per-portal creation and sign-in timestamps that
    /// <c>01.00.02.SqlDataProvider</c> lines 254-283 moved off <c>dbo.Users</c>. Empty rather
    /// than <see langword="null"/> when a read path does not include it, so callers branch on
    /// count rather than on nullness.
    /// </remarks>
    public ICollection<UserPortal> UserPortals { get; } = new List<UserPortal>();

    /// <summary>
    /// Gets this user's profile property values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Replaces the lazily hydrated profile wrapper whose getter called into a profile controller
    /// on first read (<c>UserInfo.vb</c> lines 237-245). Profile data is a key-and-value row set
    /// keyed by profile-property definition, so it is a collection here rather than nineteen
    /// fixed properties.
    /// </para>
    /// <para>
    /// This is where the address and telephone data that <c>dbo.Users</c> once held now lives.
    /// Those columns - <c>Street</c>, <c>City</c>, <c>Region</c>, <c>PostalCode</c>,
    /// <c>Country</c>, <c>Unit</c> and <c>Telephone</c> - were dropped by
    /// <c>02.02.01.SqlDataProvider</c> lines 50-51 and must not be reintroduced as scalars on
    /// this type. Note that <see cref="FirstName"/> and <see cref="LastName"/> are the exception:
    /// they remained real <c>dbo.Users</c> columns even though the legacy property getters routed
    /// them through the profile wrapper.
    /// </para>
    /// </remarks>
    public ICollection<UserProfileValue> UserProfileValues { get; } = new List<UserProfileValue>();

    /// <summary>
    /// Gets this user's role assignments.
    /// </summary>
    /// <remarks>
    /// The join to <c>dbo.UserRoles</c>, carrying the effective and expiry dates that a plain
    /// list of role names could not express. It replaces the legacy string-array role property
    /// and the string-comparison role test described in the migration note above. Authorisation
    /// is not decided here: these rows are the input from which the API layer's policy handlers
    /// build claims.
    /// </remarks>
    public ICollection<UserRole> UserRoles { get; } = new List<UserRole>();

    /// <summary>
    /// Gets the module permission entries granted directly to this user.
    /// </summary>
    /// <remarks>
    /// DotNetNuke grants a module permission either to a role or to an individual user, so the
    /// permission row references a user directly and this navigation is the user side of that
    /// relationship. Holding the entries is all this type does with them; evaluating them is the
    /// Infrastructure permission evaluator's job, driven by the API layer's authorisation
    /// policies.
    /// </remarks>
    public ICollection<ModulePermission> ModulePermissions { get; } = new List<ModulePermission>();

    /// <summary>
    /// Gets the tab permission entries granted directly to this user.
    /// </summary>
    /// <remarks>
    /// The page-level counterpart to <see cref="ModulePermissions"/>, with the same
    /// role-or-individual-user grant shape and the same division of labour: this type carries the
    /// entries and never interprets them.
    /// </remarks>
    public ICollection<TabPermission> TabPermissions { get; } = new List<TabPermission>();
}
