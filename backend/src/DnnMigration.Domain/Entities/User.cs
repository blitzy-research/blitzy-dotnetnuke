using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

// MIGRATION: the legacy null-sentinel system is not carried forward.

/// <summary>A DotNetNuke user: the aggregate root for identity, credentials and portal membership.</summary>
/// <remarks>
/// <para>
/// The first store is the DotNetNuke <c>dbo.Users</c> table, which in its terminal form holds exactly nine
/// columns. Those nine are the only members of this type that an entity configuration may map, and they are
/// grouped together below under the banner comment "PART A - TERMINAL dbo.Users COLUMNS".
/// </para>
/// <para>
/// The second store is the ASP.NET 2.0 membership schema - <c>dbo.aspnet_Users</c> and
/// <c>dbo.aspnet_Membership</c>, keyed by <c>Username</c> rather than by <c>UserID</c>. Those tables are
/// installed by Microsoft's ASP.NET SQL registration payload, not by DotNetNuke.
/// </para>
/// </remarks>
public sealed class User : Entity<int>
{
    // These nine properties, and only these nine, are mapped by UserConfiguration to dbo.Users.

    /// <summary>Gets or sets the identifier of this user, unique across the whole installation.</summary>
    /// <value>
    /// The value of the <c>UserID</c> column: <c>int IDENTITY(1, 1) NOT NULL</c>, established at
    /// <c>01.00.00.SqlDataProvider</c> line 98 and preserved through both table rebuilds
    /// (<c>01.00.05.SqlDataProvider</c> line 16 and <c>01.00.06.SqlDataProvider</c> line 184).
    /// </value>
    /// <remarks>
    /// The primary key. Declared <c>PK_Users PRIMARY KEY NONCLUSTERED</c> at
    /// <c>01.00.00.SqlDataProvider</c> lines 477-481 and promoted to <c>CLUSTERED</c> at
    /// <c>03.00.13.SqlDataProvider</c> lines 23-32.
    /// </remarks>
    public int UserId { get; set; }

    /// <summary>Gets or sets the login name of this user.</summary>
    /// <value>
    /// The value of the <c>Username</c> column: <c>nvarchar(100) NOT NULL</c>, added by the second table
    /// rebuild at <c>01.00.06.SqlDataProvider</c> line 197.
    /// </value>
    /// <remarks>
    /// Unique across the installation. The unique constraint originally covered <c>Email</c>
    /// (<c>01.00.00.SqlDataProvider</c> lines 482-485); <c>01.00.07.SqlDataProvider</c> lines 76-84 drop it
    /// and recreate it over <c>Username</c>, and <c>03.00.09.SqlDataProvider</c> lines 420-422 restate it
    /// in the templated form as <c>UNIQUE NONCLUSTERED ([Username])</c>.
    /// </remarks>
    public string Username { get; set; }

    /// <summary>Gets or sets the given name of this user.</summary>
    /// <value>
    /// The value of the <c>FirstName</c> column: <c>nvarchar(50) NOT NULL</c>, present since
    /// <c>01.00.00.SqlDataProvider</c> line 99 and carried through both rebuilds
    /// (<c>01.00.06.SqlDataProvider</c> line 185).
    /// </value>
    public string FirstName { get; set; }

    /// <summary>Gets or sets the family name of this user.</summary>
    /// <value>The value of the <c>LastName</c> column: <c>nvarchar(50) NOT NULL</c>.</value>
    public string LastName { get; set; }

    /// <summary>Gets or sets the name shown for this user in the user interface.</summary>
    /// <value>
    /// The value of the <c>DisplayName</c> column: <c>nvarchar(128) NOT NULL</c> with a database default of
    /// the empty string, added at <c>03.02.03.SqlDataProvider</c> lines 628-632 as <c>DisplayName
    /// nvarchar(128) NOT NULL CONSTRAINT DF_Users_DisplayName DEFAULT ''</c> and added again, guarded by a
    /// version check, at <c>04.00.04.SqlDataProvider</c> lines 666-673 for installations that skipped the
    /// earlier script.
    /// </value>
    /// <remarks>
    /// The database default is the empty string, not <see langword="null"/>, so an unset display name is
    /// legitimately empty text. That is not a null sentinel to be normalised away: the column is <c>NOT
    /// NULL</c>, the empty string is the value the database actually stores, and the property is
    /// correspondingly non-nullable.
    /// </remarks>
    public string DisplayName { get; set; }

    /// <summary>Gets or sets the email address recorded for this user, which may be absent.</summary>
    /// <value>
    /// The value of the <c>Email</c> column: <c>nvarchar(256) NULL</c>, re-added at
    /// <c>03.00.13.SqlDataProvider</c> lines 109-110.
    /// </value>
    /// <remarks>
    /// The type is deliberately a plain nullable string and not the <c>EmailAddress</c> value object this
    /// layer also defines. Two measured facts force that.
    /// </remarks>
    public string? Email { get; set; }

    /// <summary>Gets or sets a value indicating whether this user is a host-level super user.</summary>
    /// <value>
    /// The value of the <c>IsSuperUser</c> column: <c>bit NOT NULL</c> with a database default of <c>0</c>.
    /// </value>
    /// <remarks>
    /// Added at <c>01.00.02.SqlDataProvider</c> lines 242-243 as <c>IsSuperUser bit NOT NULL CONSTRAINT
    /// DF_Users_IsSuperUser DEFAULT (0)</c>, and re-asserted after the templating rename at
    /// <c>03.01.01.SqlDataProvider</c> lines 1342-1353, which drops whatever default constraint it finds,
    /// restates the column as <c>[bit] NOT NULL</c> and re-adds the zero default.
    /// </remarks>
    public bool IsSuperUser { get; set; }

    /// <summary>Gets or sets the identifier of the affiliate that referred this user, if any.</summary>
    /// <value>
    /// The value of the <c>AffiliateID</c> column: <c>int NULL</c>, added at
    /// <c>02.00.00.SqlDataProvider</c> lines 6957-6958.
    /// </value>
    /// <remarks>
    /// Genuinely optional - most users are not referred - and mapped as <see cref="System.Nullable{T}"/> of
    /// <see cref="int"/> so that absence is expressed by <see langword="null"/> rather than by a magic
    /// number.
    /// </remarks>
    public int? AffiliateId { get; set; }

    /// <summary>Gets or sets a value indicating whether this user must change password at next sign-in.</summary>
    /// <value>
    /// The value of the <c>UpdatePassword</c> column: <c>bit NOT NULL</c> with a database default of
    /// <c>0</c>.
    /// </value>
    /// <remarks>
    /// Added alongside <see cref="DisplayName"/> at <c>03.02.03.SqlDataProvider</c> lines 628-632 as
    /// <c>UpdatePassword bit NOT NULL CONSTRAINT DF_Users_UpdatePassword DEFAULT 0</c>, and again under a
    /// version guard at <c>04.00.04.SqlDataProvider</c> lines 666-673.
    /// </remarks>
    public bool UpdatePassword { get; set; }

    // =====================================================================================
    // IDENTITY (equality only - not a mapped column)
    // =====================================================================================

    /// <inheritdoc />
    /// <remarks>
    /// Forwards <see cref="UserId"/> so that <see cref="Entity{TId}"/> can compare two users by identity.
    /// This member exists for equality alone: it is get-only, it carries no attribute, and no entity
    /// configuration binds it to a column. <c>UserConfiguration</c> maps <see cref="UserId"/> and names
    /// that property in its explicit key declaration instead.
    /// </remarks>
    public override int Identity => UserId;

    // PART B - EXTERNAL AND CONTEXTUAL MEMBERSHIP SNAPSHOT (11)
    // The invariant, and it admits no partial satisfaction - UserConfiguration excludes every property in
    // this section from the model, and the executable list of exclusions lives there rather than being
    // copied here, where a second copy could disagree with the first.

    /// <summary>
    /// Gets or sets the composed approval state of this user, or <see langword="null"/> when it was not
    /// composed on the read path that produced this instance.
    /// </summary>
    /// <remarks>
    /// CONTEXT-SENSITIVE. There is no single authoritative <c>dbo.Users</c> column behind this property,
    /// and treating it as though there were is the most likely way to get authorisation wrong. Which store
    /// answers depends on <see cref="IsSuperUser"/>. For an ordinary portal user the answer is the
    /// per-portal <c>UserPortals.Authorised</c> column - British spelling in the schema, added at
    /// <c>03.02.03.SqlDataProvider</c> lines 637-641 - carried here as the authorisation flag on the
    /// <c>UserPortal</c> join entity and reachable through <see cref="UserPortals"/>. The legacy provider
    /// reads it only under <c>If Not objUserInfo.IsSuperUser</c>
    /// (<c>AspNetMembershipProvider.vb</c> lines 355-361), so a host account's approval does not come
    /// from that column. An authorisation decision must read the flag belonging to the portal it is
    /// deciding.
    /// </remarks>
    public bool? IsApproved { get; set; }

    /// <summary>
    /// Gets or sets the composed creation timestamp of this user's membership record, or <see
    /// langword="null"/> when it was not composed on this read path.
    /// </summary>
    /// <remarks>
    /// This is external membership data and NOT a common audit field, which is why this type derives from
    /// <see cref="Entity{TId}"/> and deliberately not from an auditable base class. Two facts make that
    /// unavoidable.
    /// </remarks>
    public DateTime? CreatedDate { get; set; }

    /// <summary>
    /// Gets or sets the composed online state of this user at the moment the query ran, or <see
    /// langword="null"/> when it was not composed on this read path.
    /// </summary>
    /// <remarks>
    /// This property must never read the current time, must never have a clock injected beside it, and must
    /// never perform any I/O - Rule T6. The Domain layer offers a clock abstraction precisely so that
    /// time-dependent decisions are made in a testable service; a record does not consult one.
    /// </remarks>
    public bool? IsOnline { get; set; }

    /// <summary>
    /// Gets or sets the composed timestamp of this user's last recorded activity, or <see langword="null"/>
    /// when it was not composed on this read path.
    /// </summary>
    /// <remarks>
    /// This is the input from which <see cref="IsOnline"/> is derived. Keeping the raw timestamp alongside
    /// the derived flag is deliberate: a caller that knows the current time and the configured window can
    /// recompute online-ness for itself, which the boolean alone would not permit.
    /// </remarks>
    public DateTime? LastActivityDate { get; set; }

    /// <summary>
    /// Gets or sets the composed timestamp of this user's most recent lockout, or <see langword="null"/>
    /// when it was not composed on this read path.
    /// </summary>
    public DateTime? LastLockoutDate { get; set; }

    /// <summary>
    /// Gets or sets the composed timestamp of this user's most recent sign-in, or <see langword="null"/>
    /// when it was not composed on this read path.
    /// </summary>
    /// <remarks>
    /// Like <see cref="CreatedDate"/>, this value has migrated between stores: <c>dbo.Users</c> held a
    /// <c>LastLoginDate</c> column at <c>01.00.00.SqlDataProvider</c> line 109, which was copied to
    /// <c>UserPortals</c> and dropped at <c>01.00.02.SqlDataProvider</c> lines 254-283, then dropped from
    /// <c>UserPortals</c> too at <c>02.02.01.SqlDataProvider</c> lines 54-55 after the upgrade reconciled
    /// it into <c>aspnet_Membership</c> at lines 28-34 of that same script.
    /// </remarks>
    public DateTime? LastLoginDate { get; set; }

    /// <summary>
    /// Gets or sets the composed timestamp of this user's most recent password change, or <see
    /// langword="null"/> when it was not composed on this read path.
    /// </summary>
    /// <remarks>
    /// In the target this is the natural place to observe that a stored credential predates the migration,
    /// which matters because such an account can only be recovered by the administrative reset described on
    /// <see cref="PasswordHash"/>. It pairs with <see cref="UpdatePassword"/>, the one credential fact that
    /// really is a <c>dbo.Users</c> column, to drive password-age policy in the Application layer.
    /// </remarks>
    public DateTime? LastPasswordChangeDate { get; set; }

    /// <summary>
    /// Gets or sets the composed lockout state of this user, or <see langword="null"/> when it was not
    /// composed on this read path.
    /// </summary>
    /// <remarks>
    /// Lockout is produced by the failed-attempt counters that live entirely in the external store -
    /// <c>FailedPasswordAttemptCount</c>, <c>FailedPasswordAttemptWindowStart</c>,
    /// <c>FailedPasswordAnswerAttemptCount</c> and <c>FailedPasswordAnswerAttemptWindowStart</c> at
    /// <c>InstallMembership.sql</c> lines 96-99, maintained by the membership procedures that the
    /// DotNetNuke chain ALTERs rather than creates (<c>04.00.00.SqlDataProvider</c> lines 31 and 119).
    /// </remarks>
    public bool? IsLockedOut { get; set; }

    /// <summary>
    /// Gets or sets the one-way hash of this user's password, or <see langword="null"/> when it was not
    /// composed on this read path.
    /// </summary>
    /// <remarks>
    /// Password RETRIEVAL is deliberately not carried forward, in any form. There is no member here that
    /// returns a password, no operation that decrypts one, and no path by which this value may be projected
    /// into a response DTO, written to a log, or included in an error message.
    /// </remarks>
    public string? PasswordHash { get; set; }

    /// <summary>
    /// Gets or sets the legacy password answer held in the external membership store, or <see
    /// langword="null"/> when it was not composed on this read path.
    /// </summary>
    /// <remarks>
    /// This value must never be returned in an API DTO, never be written to a log, an audit entry, a trace
    /// or an exception message, and never be used by the new authentication path. Password recovery is not
    /// carried forward at all - see <see cref="PasswordHash"/> - so nothing in the target has a legitimate
    /// reason to read it.
    /// </remarks>
    public string? PasswordAnswer { get; set; }

    /// <summary>
    /// Gets or sets the legacy password question held in the external membership store, or <see
    /// langword="null"/> when it was not composed on this read path.
    /// </summary>
    public string? PasswordQuestion { get; set; }

    // NAVIGATIONS (5)
    // No scalar PortalId appears anywhere on this type. A user is one row in dbo.Users shared by every
    // portal it belongs to; the per-portal facts - membership, authorisation, creation and last sign-in -
    // live on the UserPortals row.

    /// <summary>Gets this user's per-portal membership rows.</summary>
    /// <remarks>
    /// The join to <c>dbo.UserPortals</c>, and the only correct home for per-portal facts: the portal
    /// identifier, the <c>Authorised</c> flag behind <see cref="IsApproved"/> for ordinary users, and the
    /// per-portal creation and sign-in timestamps that <c>01.00.02.SqlDataProvider</c> lines 254-283 moved
    /// off <c>dbo.Users</c>.
    /// </remarks>
    public ICollection<UserPortal> UserPortals { get; } = new List<UserPortal>();

    /// <summary>Gets this user's profile property values.</summary>
    public ICollection<UserProfileValue> UserProfileValues { get; } = new List<UserProfileValue>();

    /// <summary>Gets this user's role assignments.</summary>
    /// <remarks>
    /// The join to <c>dbo.UserRoles</c>, carrying the effective and expiry dates that a plain list of role
    /// names could not express. It replaces the legacy string-array role property and the string-comparison
    /// role test described in the migration note above.
    /// </remarks>
    public ICollection<UserRole> UserRoles { get; } = new List<UserRole>();

    /// <summary>Gets the module permission entries granted directly to this user.</summary>
    /// <remarks>
    /// DotNetNuke grants a module permission either to a role or to an individual user, so the permission
    /// row references a user directly and this navigation is the user side of that relationship. Holding
    /// the entries is all this type does with them; evaluating them is the Infrastructure permission
    /// evaluator's job, driven by the API layer's authorisation policies.
    /// </remarks>
    public ICollection<ModulePermission> ModulePermissions { get; } = new List<ModulePermission>();

    /// <summary>Gets the tab permission entries granted directly to this user.</summary>
    public ICollection<TabPermission> TabPermissions { get; } = new List<TabPermission>();
}
