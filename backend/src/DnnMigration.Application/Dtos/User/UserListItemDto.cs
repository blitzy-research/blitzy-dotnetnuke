namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// One row of the user administration grid, returned by <c>GET /api/v1/users</c> as the element type
/// of the paged response envelope.
/// </summary>
/// <remarks>
/// <para>
/// The member set and its declaration order are measured from the legacy grid, so rendering the
/// properties in declaration order reproduces the legacy column order. Where a member name differs
/// from the legacy grid header the difference is noted on the member, because the header spelling
/// forms a settings key.
/// </para>
/// <para>
/// Per-column visibility is applied before this contract is returned. The legacy screen resolved its
/// <c>Column_&lt;header&gt;</c> module settings client-side; the service enforces them instead of
/// trusting every client to hide sensitive values, replacing a hidden text field with the empty
/// string and a hidden nullable field with <see langword="null"/>. There is deliberately no
/// visibility flag on this type: the sensitive value itself must not cross the boundary merely
/// because a cooperative client could choose not to render it.
/// </para>
/// <para>
/// Several members are not user-row data. <see cref="PortalId"/> comes from the route or resolved
/// portal context; <see cref="Address"/> and <see cref="Telephone"/> are composed from the profile
/// key-value store; and <see cref="CreatedDate"/>, <see cref="LastLoginDate"/>,
/// <see cref="IsOnline"/> and <see cref="IsLockedOut"/> originate in the externally installed
/// ASP.NET membership store.
/// </para>
/// <para>
/// This type is where the legacy sentinel encodings are translated. Absent text surfaces as
/// <see cref="string.Empty"/>, preserving the legacy observable value, and an absent timestamp
/// surfaces as <see langword="null"/> so no client is handed <c>0001-01-01</c> as a real instant.
/// The host serialises with
/// <see cref="System.Text.Json.Serialization.JsonIgnoreCondition.Never"/>, so every member is written
/// and absence is signalled by a member's VALUE rather than by the member being missing - a policy
/// that dropped nulls or defaults could not be trusted to leave a legitimate <c>0</c>, <c>false</c>
/// or empty string alone.
/// </para>
/// <para>
/// SECURITY: no credential material of any kind appears here - no stored credential, digest, salt,
/// format, recovery question or answer - and none may be added.
/// </para>
/// <para>
/// No paging member appears here; the envelope owns the item list, total count, page index and page
/// size. Also absent: the create-status and login-status enumerations (an expected failure is a
/// service result rendered as a problem document at the edge), the correlation identifier (a response
/// header), the affiliate identifier (the grid never rendered it) and the role set (the detail
/// contract owns it).
/// </para>
/// </remarks>
public sealed class UserListItemDto
{
    /// <summary>
    /// Gets or sets the user's identifier, from <c>Users.UserID</c>
    /// (<c>int IDENTITY (1, 1) NOT NULL</c>).
    /// </summary>
    /// <remarks>
    /// Presence is decided by the envelope containing the row, never by inspecting this value: the
    /// legacy constructor seeded the field with the integer sentinel -1, so a non-positive value is
    /// not evidence of absence.
    /// </remarks>
    public int UserId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the portal whose user list this row belongs to.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this is NOT a column on the legacy <c>Users</c> table - per-portal facts live on
    /// <c>UserPortals</c>, which the terminal <c>vw_Users</c> view reaches through a LEFT OUTER JOIN
    /// that could yield SQL <c>NULL</c> for a host user with no membership row, which the legacy read
    /// collapsed to -1: the exact value that is also a real portal key. Populate this member from the
    /// route or the resolved portal context, never from the user entity.
    /// <para>
    /// IDENTIFIER TRAP: <c>Portals.PortalID</c> is <c>IDENTITY (-1, 1)</c> and the shipped default
    /// portal is inserted explicitly as 0, so both are valid keys while -1 is also the legacy
    /// absent-integer sentinel. Both <c>id &lt;= 0</c> and <c>id == -1</c> are therefore invalid
    /// absence tests, here and against any portal, role, tab or module identifier. Absence is
    /// expressed by a nullable type or an empty result, never by a magic number.
    /// </para>
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>
    /// Gets or sets the login name, from <c>Users.Username</c> (<c>nvarchar(100) NOT NULL</c>).
    /// Always rendered by the legacy grid, which has no visibility setting for this column.
    /// </summary>
    /// <remarks>
    /// UNIQUENESS BELONGS HERE AND NOT TO <see cref="Email"/>: the upgrade chain moved the unique
    /// constraint off the email column onto this one, matching the legacy membership registration,
    /// which did not require a unique email address. The 100-character ceiling is the schema's, and
    /// enforcing it belongs to the validators rather than to this type.
    /// </remarks>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the given name, from <c>Users.FirstName</c> (<c>nvarchar(50) NOT NULL</c>).
    /// </summary>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the family name, from <c>Users.LastName</c> (<c>nvarchar(50) NOT NULL</c>).
    /// </summary>
    /// <remarks>
    /// The terminal nullability is not the baseline's: the baseline declared the column
    /// <c>NULL</c>, and the table rebuild that followed re-declared it <c>NOT NULL</c>. The
    /// cumulative terminal schema is the binding authority, so this member is non-nullable and
    /// absent text is the empty string.
    /// </remarks>
    public string LastName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the canonical display name, from <c>Users.DisplayName</c>
    /// (<c>nvarchar(128) NOT NULL</c> defaulting to the empty string - which is why that is the
    /// value this member carries when unset).
    /// </summary>
    /// <remarks>
    /// Supersedes the obsolete computed full name. The legacy display-name token substitution is a
    /// service concern and is deliberately not performed here: this member carries the
    /// already-resolved value.
    /// </remarks>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the pre-composed postal address for display.</summary>
    /// <remarks>
    /// Two distinct absences, both preserved: <see langword="null"/> means the portal's profile
    /// definitions carry no address data for this user at all, while an empty string means address
    /// properties exist but compose to nothing, which is the legacy behaviour. Under the host's
    /// <c>Never</c> ignore policy the two stay distinguishable by VALUE. The legacy grid composed
    /// this from six separate profile properties; composing it in an accessor here is forbidden,
    /// because this type does no work.
    /// </remarks>
    public string? Address { get; set; }

    /// <summary>Gets or sets the telephone number for display.</summary>
    /// <remarks>
    /// As with <see cref="Address"/>, <see langword="null"/> means the profile carries no telephone
    /// property and an empty string means the property exists and is blank; both states stay
    /// distinct on the wire.
    /// </remarks>
    public string? Telephone { get; set; }

    /// <summary>Gets or sets the email address, from <c>Users.Email</c>.</summary>
    /// <remarks>
    /// <para>
    /// The terminal column is <c>nvarchar(256) NULL</c>, reached by a drop and re-create -
    /// VALIDATORS MUST USE 256, since the 100-character figure is the superseded baseline. The
    /// field carries no uniqueness guarantee, so two users in one portal may legitimately share an
    /// address and no consumer may treat it as a key.
    /// </para>
    /// <para>
    /// Although the terminal column is nullable this member is not: the legacy property was
    /// declared required and every legacy read rendered absent text as the empty string, so the
    /// externally observable value for a missing address has always been
    /// <see cref="string.Empty"/>. Emitting a null here would be a silent change to an observable
    /// value.
    /// </para>
    /// </remarks>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the instant the account was created, or <see langword="null"/> when unknown.
    /// </summary>
    /// <remarks>
    /// Test for null and never compare against <see cref="DateTime.MinValue"/>: that value is the
    /// legacy absent-date sentinel, translated to <see langword="null"/> at this boundary so that
    /// no client is handed <c>0001-01-01</c> as a real timestamp. The membership store names the
    /// same value <c>CreationDate</c>; the legacy rename is preserved here.
    /// </remarks>
    public DateTime? CreatedDate { get; set; }

    /// <summary>
    /// Gets or sets the instant of the most recent successful sign-in, or <see langword="null"/>
    /// when the account has never signed in.
    /// </summary>
    /// <remarks>
    /// A never-signed-in account is the ordinary reason for <see langword="null"/>, and it is a
    /// materially different state from "signed in at the dawn of the calendar", which is what the
    /// untranslated legacy sentinel would have implied.
    /// </remarks>
    // MIGRATION: the legacy grid header is "LastLogin", and that spelling is load-bearing because it forms
    // the Column_LastLogin visibility key. The settings contract therefore keeps the short name while this
    // member takes the longer name of the actual column.
    public DateTime? LastLoginDate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the account is approved for the portal - that is,
    /// authorised to sign in.
    /// </summary>
    /// <remarks>
    /// The legacy grid rendered this as a pair of mutually exclusive images, so both states are
    /// meaningful and neither may be dropped from the payload.
    /// </remarks>
    // MIGRATION: one member reconciles four legacy spellings of one concept - the grid header "Authorized"
    // (which forms the Column_Authorized settings key), the legacy property Approved without the Is prefix,
    // the baseline US-spelled column UserPortals.Authorized that the upgrade chain dropped, and the terminal
    // UK-spelled UserPortals.Authorised. A maintainer searching the schema for the US spelling will not find
    // it.
    //
    // MIGRATION: the legacy default is true at both the property and the column, whereas this member
    // defaults to false. That is deliberate and fail-safe: the mapper always populates a response row, so
    // the member default is never observed on the wire, and defaulting to true could report an unapproved
    // account as approved.
    public bool IsApproved { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user is currently considered online. Advisory
    /// only: the legacy scheduled job that purged the presence table is outside this scope, so a
    /// stale presence row is possible.
    /// </summary>
    /// <remarks>
    /// This drove the legacy grid's online indicator. It has no <c>Column_</c> visibility setting
    /// because it is an indicator rather than a toggleable data column, and the legacy property
    /// spelled it <c>IsOnLine</c>, with a capital L in the middle.
    /// </remarks>
    public bool IsOnline { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user is a host-level super user, whose reach
    /// spans every portal rather than one. From <c>Users.IsSuperUser</c> (<c>bit NOT NULL</c>
    /// defaulting to 0).
    /// </summary>
    /// <remarks>
    /// The list row genuinely needs it: the legacy delete affordance was suppressed not only for
    /// the portal administrator but also when the row was the acting user and that user was a super
    /// user, so a client cannot reproduce the legacy action set without this flag. Advisory for
    /// rendering only - every authorisation decision is taken on the server.
    /// </remarks>
    public bool IsSuperUser { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the account is under a lockout following repeated
    /// failed sign-in attempts.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="IsApproved"/>: an approved account can be under a lockout, and an
    /// unapproved one need not be. Carried because a list that cannot distinguish a locked account
    /// from an unapproved one loses information the legacy screens conveyed. The lockout
    /// bookkeeping is maintained by membership procedures that the legacy upgrade scripts patch
    /// rather than create, and the legacy property spelled it <c>LockedOut</c>, without the Is
    /// prefix.
    /// </remarks>
    public bool IsLockedOut { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this row may be deleted by an administrator of the
    /// portal it belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A CAPABILITY, computed against the same two protections <c>DeleteUserAsync</c> enforces: an
    /// account flagged <see cref="IsSuperUser"/> is refused with <c>user.delete.super-user-protected</c>,
    /// and the account named by <c>Portals.AdministratorId</c> is refused with
    /// <c>user.delete.administrator-protected</c>. Publishing it is what lets a client withhold an
    /// affordance the server would refuse, rather than offering a command whose only outcome is a 403.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy grid suppressed its delete image per row at <c>Users.ascx.vb:L691-L692</c>
    /// with <c>Not (user.UserID = PortalSettings.AdministratorId) AndAlso Not (user.UserID = Me.UserId
    /// And user.IsSuperUser)</c>. The first protection is reproduced exactly. The second is
    /// DELIBERATELY WIDER here: the legacy hid the affordance for a super-user row only when that row
    /// was also the account named by the screen's own <c>UserId</c> parameter - which on the listing was
    /// the legacy null marker unless a caller supplied one, so in ordinary use the clause was
    /// unreachable and every other super-user row was offered a delete the operation would then have to
    /// deal with. This flag reports what the server will actually do, so every super-user row is
    /// withheld. Reporting the narrower legacy predicate would produce an affordance that fails.
    /// </para>
    /// <para>
    /// ADVISORY FOR RENDERING ONLY. The server re-evaluates both protections on the delete itself, so a
    /// client that ignores this flag is refused rather than obeyed.
    /// </para>
    /// </remarks>
    public bool CanDelete { get; set; }
}
