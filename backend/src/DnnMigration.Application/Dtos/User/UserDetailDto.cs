namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// The full detail projection of a single portal user, returned by <c>GET /api/v1/users/{userId}</c>.
/// </summary>
/// <remarks>
/// <b>Nullability and width follow the terminal database schema</b>, not the attributes decorating the
/// legacy classes; where the two disagree the schema wins and the disagreement is recorded on the affected
/// member. Widths are quoted for the benefit of the validators in Application/Validation and are
/// deliberately not encoded as attributes, because this type carries no validation of its own.
/// </remarks>
public sealed class UserDetailDto
{
    // Identity and tenancy

    /// <summary>The surrogate key of the user, from <c>Users.UserID</c>.</summary>
    /// <remarks>
    /// Declared <c>IDENTITY(1,1) NOT NULL</c> (01.00.00.SqlDataProvider L98), so a persisted user always
    /// has a positive identifier and this member is never absent on a successful response. The legacy name
    /// is retained rather than generalised to <c>Id</c>, so a reader can line this contract up against the
    /// schema and the legacy code without a mapping table.
    /// </remarks>
    public int UserId { get; set; }

    /// <summary>The portal (tenant) whose membership this projection describes.</summary>
    /// <remarks>
    /// <b>Do not test this value for absence.</b> <c>Portals.PortalID</c> is declared <c>IDENTITY(-1,
    /// 1)</c> (01.00.00.SqlDataProvider L77), so the seed and first generated value is <c>-1</c>, while the
    /// shipped default portal row is inserted explicitly with <c>PortalID</c> <c>0</c> - both are valid
    /// keys.
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>The user's login name, from <c>Users.Username</c>.</summary>
    /// <remarks>
    /// <c>nvarchar(100) NOT NULL</c> (01.00.06.SqlDataProvider L197) and the only uniquely constrained user
    /// attribute: <c>ADD CONSTRAINT [IX_{objectQualifier}Users] UNIQUE NONCLUSTERED ([Username])</c>
    /// (03.00.09.SqlDataProvider L422). <b>Username is unique; <see cref="Email"/> is not</b> - the two
    /// must not be treated interchangeably as an account key.
    /// </remarks>
    public string Username { get; set; } = string.Empty;

    // Name and contact

    /// <summary>The user's given name, from <c>Users.FirstName</c>.</summary>
    /// <remarks>
    /// <c>nvarchar(50) NOT NULL</c>, unchanged in width and nullability from its original declaration at
    /// 01.00.00.SqlDataProvider L99 through both table rebuilds, so this member is non-nullable and
    /// defaults to the empty string. <see cref="LastName"/> reaches the same terminal shape by a different
    /// route.
    /// </remarks>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>The user's family name, from <c>Users.LastName</c>.</summary>
    public string LastName { get; set; } = string.Empty;

    /// <summary>The user's presentation name, from <c>Users.DisplayName</c>.</summary>
    /// <remarks>
    /// <c>nvarchar(128) NOT NULL CONSTRAINT DF_{objectQualifier}Users_DisplayName DEFAULT ''</c>
    /// (03.02.03.SqlDataProvider L630), so this member is non-nullable and defaults to the empty string,
    /// matching the column default exactly.
    /// </remarks>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The user's email address, from <c>Users.Email</c>.</summary>
    /// <remarks>
    /// Terminal schema is <c>Email nvarchar(256) NULL</c>. Unlike <see cref="Username"/> it carries <b>no
    /// unique constraint</b>, which matches the legacy membership provider being registered with
    /// <c>requiresUniqueEmail="false"</c>: two accounts may legitimately share an address.
    /// </remarks>
    public string Email { get; set; } = string.Empty;

    // Elevation and attribution

    /// <summary>Whether the user is a host-level super user, from <c>Users.IsSuperUser</c>.</summary>
    public bool IsSuperUser { get; set; }

    /// <summary>
    /// The affiliate the user was referred by, from <c>Users.AffiliateId</c>, or <see langword="null"/>
    /// when the user was not referred.
    /// </summary>
    /// <remarks>
    /// The legacy representation of "not referred" was the integer sentinel <c>-1</c>, assigned in the
    /// <c>UserInfo</c> constructor; the mapper translates it to <see langword="null"/> so absence is
    /// expressed in the type system rather than by a magic number.
    /// </remarks>
    public int? AffiliateId { get; set; }

    /// <summary>Whether the user's membership has been approved for the portal.</summary>
    public bool IsApproved { get; set; }

    /// <summary>Whether the account is currently locked out after failed sign-in attempts.</summary>
    public bool IsLockedOut { get; set; }

    /// <summary>Whether the user is currently considered online.</summary>
    /// <remarks>
    /// Renamed from the legacy <c>IsOnLine</c> (membership composite L123) - note the internal capital L in
    /// the original - to the conventional <c>IsOnline</c>; the meaning is unchanged. Required by the client
    /// to render the online status indicator.
    /// </remarks>
    public bool IsOnline { get; set; }

    /// <summary>Whether the user must change their password before continuing.</summary>
    /// <remarks>
    /// <b>renamed</b> from the legacy <c>UpdatePassword</c> (membership composite L323), backed by
    /// <c>UpdatePassword bit NOT NULL CONSTRAINT DF_{objectQualifier}Users_UpdatePassword DEFAULT 0</c>
    /// (03.02.03.SqlDataProvider L631).
    /// </remarks>
    public bool MustChangePassword { get; set; }

    // Membership dates

    /// <summary>When the user record was created, or <see langword="null"/> if unknown.</summary>
    /// <remarks>
    /// <c>Users.CreatedDate datetime NULL</c> (01.00.00.SqlDataProvider L108), surfaced by the legacy
    /// composite as a read-only property. Nullable both because the column is and because the legacy
    /// sentinel must not reach a client.
    /// </remarks>
    public DateTime? CreatedDate { get; set; }

    /// <summary>When the user last signed in successfully, or <see langword="null"/> if they never have.</summary>
    /// <remarks>
    /// <c>Users.LastLoginDate datetime NULL</c> (01.00.00.SqlDataProvider L109), read-only on the legacy
    /// composite. The <see langword="null"/> case is the ordinary one for a newly created account and is
    /// exactly where the legacy sentinel would otherwise surface as the year 1.
    /// </remarks>
    public DateTime? LastLoginDate { get; set; }

    /// <summary>When the user was last active, or <see langword="null"/> if never recorded.</summary>
    /// <remarks>
    /// Read-only on the legacy composite. Composed externally rather than read from a <c>Users</c> column,
    /// and nullable for the same sentinel reason as the other dates.
    /// </remarks>
    public DateTime? LastActivityDate { get; set; }

    /// <summary>When the account was last locked out, or <see langword="null"/> if it never has been.</summary>
    public DateTime? LastLockoutDate { get; set; }

    /// <summary>When the user's password was last changed, or <see langword="null"/> if it never has been.</summary>
    public DateTime? LastPasswordChangeDate { get; set; }

    // Role membership

    /// <summary>
    /// The names of the roles the user holds in the portal. Never <see langword="null"/>; an empty list
    /// means the user holds none.
    /// </summary>
    /// <remarks>
    /// The legacy <c>UserInfo.Roles</c> was typed <c>String()</c> - a mutable array of role <i>names</i>.
    /// It is exposed here as a read-only list so a caller cannot mutate a response object in place,
    /// defaulting to an empty array because the legacy representation of "no roles" was an empty set and a
    /// client should not have to null-check before iterating.
    /// </remarks>
    public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();

    /// <summary>Whether this account may be removed from the tenant.</summary>
    /// <remarks>
    /// ⚠ PUBLISHED BECAUSE THE CLIENT CANNOT DERIVE IT. The removal operation refuses a super user and it
    /// refuses the account named by <c>Portals.AdministratorId</c>, and nothing else in this contract
    /// reveals who that designated administrator is - so a screen editing one account had no way to reach
    /// the second clause.
    /// </remarks>
    public bool CanDelete { get; set; }

    /// <summary>
    /// The optimistic-concurrency token for this record: send it back on an update to be refused rather
    /// than to silently overwrite an edit someone else committed in the meantime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived from the record's own current values rather than stored in a column, because the legacy
    /// schema is immutable under the migration's Rule T4 and <c>dbo.Users</c> carries no version column. The
    /// derivation lives in <c>UserMappings.ConcurrencyTokenFor</c> and is used by both the read that
    /// publishes the token and the write that verifies it, so the two can never disagree about what the
    /// token covers.
    /// </para>
    /// <para>
    /// ⚠ IT COVERS THE FOUR MEMBERS AN UPDATE REPLACES AND NOTHING ELSE - given name, family name, display
    /// name and address. That boundary is deliberate: an account's approval state, its lock state and its
    /// last-login moment all change through paths of their own, and several of them change without any
    /// operator acting at all. A token that moved when a member simply signed in would refuse an
    /// administrator's edit for a reason that has nothing to do with the edit.
    /// </para>
    /// </remarks>
    public string ConcurrencyToken { get; set; } = string.Empty;
}
