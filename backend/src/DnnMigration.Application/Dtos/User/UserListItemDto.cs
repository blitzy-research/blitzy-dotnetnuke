namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// One row of the user administration grid, returned by <c>GET /api/v1/users</c> as the element type of the
/// paged response envelope.
/// </summary>
/// <remarks>
/// <para>
/// The member set and its declaration order are measured from the legacy grid, so rendering the properties
/// in declaration order reproduces the legacy column order. Where a member name differs from the legacy
/// grid header the difference is noted on the member, because the header spelling forms a settings key.
/// </para>
/// <para>
/// SECURITY: no credential material of any kind appears here - no stored credential, digest, salt, format,
/// recovery question or answer - and none may be added.
/// </para>
/// </remarks>
public sealed class UserListItemDto
{
    /// <summary>
    /// Gets or sets the user's identifier, from <c>Users.UserID</c> (<c>int IDENTITY (1, 1) NOT NULL</c>).
    /// </summary>
    /// <remarks>
    /// Presence is decided by the envelope containing the row, never by inspecting this value: the legacy
    /// constructor seeded the field with the integer sentinel -1, so a non-positive value is not evidence
    /// of absence.
    /// </remarks>
    public int UserId { get; set; }

    /// <summary>Gets or sets the identifier of the portal whose user list this row belongs to.</summary>
    /// <remarks>
    /// IDENTIFIER TRAP: <c>Portals.PortalID</c> is <c>IDENTITY (-1, 1)</c> and the shipped default portal
    /// is inserted explicitly as 0, so both are valid keys while -1 is also the legacy absent-integer
    /// sentinel. Both <c>id &lt;= 0</c> and <c>id == -1</c> are therefore invalid absence tests, here and
    /// against any portal, role, tab or module identifier.
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>
    /// Gets or sets the login name, from <c>Users.Username</c> (<c>nvarchar(100) NOT NULL</c>). Always
    /// rendered by the legacy grid, which has no visibility setting for this column.
    /// </summary>
    /// <remarks>
    /// UNIQUENESS BELONGS HERE AND NOT TO <see cref="Email"/>: the upgrade chain moved the unique
    /// constraint off the email column onto this one, matching the legacy membership registration, which
    /// did not require a unique email address. The 100-character ceiling is the schema's, and enforcing it
    /// belongs to the validators rather than to this type.
    /// </remarks>
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the given name, from <c>Users.FirstName</c> (<c>nvarchar(50) NOT NULL</c>).</summary>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>Gets or sets the family name, from <c>Users.LastName</c> (<c>nvarchar(50) NOT NULL</c>).</summary>
    /// <remarks>
    /// The terminal nullability is not the baseline's: the baseline declared the column <c>NULL</c>, and
    /// the table rebuild that followed re-declared it <c>NOT NULL</c>. The cumulative terminal schema is
    /// the binding authority, so this member is non-nullable and absent text is the empty string.
    /// </remarks>
    public string LastName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the canonical display name, from <c>Users.DisplayName</c> (<c>nvarchar(128) NOT
    /// NULL</c> defaulting to the empty string - which is why that is the value this member carries when
    /// unset).
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the pre-composed postal address for display.</summary>
    /// <remarks>
    /// Two distinct absences, both preserved: <see langword="null"/> means the portal's profile definitions
    /// carry no address data for this user at all, while an empty string means address properties exist but
    /// compose to nothing, which is the legacy behaviour.
    /// </remarks>
    public string? Address { get; set; }

    /// <summary>Gets or sets the telephone number for display.</summary>
    public string? Telephone { get; set; }

    /// <summary>Gets or sets the email address, from <c>Users.Email</c>.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Gets or sets the instant the account was created, or <see langword="null"/> when unknown.</summary>
    /// <remarks>
    /// Test for null and never compare against <see cref="DateTime.MinValue"/>: that value is the legacy
    /// absent-date sentinel, translated to <see langword="null"/> at this boundary so that no client is
    /// handed <c>0001-01-01</c> as a real timestamp. The membership store names the same value
    /// <c>CreationDate</c>; the legacy rename is preserved here.
    /// </remarks>
    public DateTime? CreatedDate { get; set; }

    /// <summary>
    /// Gets or sets the instant of the most recent successful sign-in, or <see langword="null"/> when the
    /// account has never signed in.
    /// </summary>
    /// <remarks>
    /// A never-signed-in account is the ordinary reason for <see langword="null"/>, and it is a materially
    /// different state from "signed in at the dawn of the calendar", which is what the untranslated legacy
    /// sentinel would have implied.
    /// </remarks>
    // The legacy grid header is "LastLogin", and that spelling is load-bearing because it forms the
    // Column_LastLogin visibility key. The settings contract therefore keeps the short name while this
    // member takes the longer name of the actual column.
    public DateTime? LastLoginDate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the account is approved for the portal - that is, authorised
    /// to sign in.
    /// </summary>
    // The legacy default is true at both the property and the column, whereas this member defaults to
    // false.
    public bool IsApproved { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user is currently considered online. Advisory only: the
    /// legacy scheduled job that purged the presence table is outside this scope, so a stale presence row
    /// is possible.
    /// </summary>
    public bool IsOnline { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user is a host-level super user, whose reach spans every
    /// portal rather than one. From <c>Users.IsSuperUser</c> (<c>bit NOT NULL</c> defaulting to 0).
    /// </summary>
    /// <remarks>
    /// The list row genuinely needs it: the legacy delete affordance was suppressed not only for the portal
    /// administrator but also when the row was the acting user and that user was a super user, so a client
    /// cannot reproduce the legacy action set without this flag. Advisory for rendering only - every
    /// authorisation decision is taken on the server.
    /// </remarks>
    public bool IsSuperUser { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the account is under a lockout following repeated failed
    /// sign-in attempts.
    /// </summary>
    public bool IsLockedOut { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this row may be deleted by an administrator of the portal it
    /// belongs to.
    /// </summary>
    public bool CanDelete { get; set; }
}
