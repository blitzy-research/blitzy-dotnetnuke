namespace DnnMigration.Application.Dtos.Auth;

/// <summary>
/// The signed-in caller's own identity and interface-gating data, returned by <c>GET /api/v1/auth/me</c>:
/// the user key, the portal identifier and its display name, the caller's names and address, the super-user
/// flag, and the advisory role and permission lists.
/// </summary>
/// <remarks>
/// <para>
/// SECURITY: no credential material and no session artefact appear on this contract, and none may ever be
/// added. This body is emitted on every application-shell render, which makes it the payload most likely to
/// be captured by request and response logging; keeping every credential-shaped member off it is what makes
/// that logging safe by construction.
/// </para>
/// <para>
/// SECURITY: <see cref="Roles"/> and <see cref="Permissions"/> are advisory only - they exist so the client
/// can hide affordances the caller cannot exercise. Authoritative enforcement is server-side policy-based
/// authorisation, so a caller who tampers with this response changes what a menu looks like and nothing
/// about what the API will permit.
/// </para>
/// </remarks>
public sealed class CurrentUserDto
{
    /// <summary>
    /// The surrogate key of the signed-in user, from <c>Users.UserID</c>, declared <c>[int] IDENTITY (1, 1)
    /// NOT NULL</c> (01.00.00.SqlDataProvider L98).
    /// </summary>
    /// <remarks>
    /// The legacy column name is retained rather than generalised to <c>Id</c>, so every identifier on
    /// every contract lines up against the schema without a translation table.
    /// </remarks>
    public int UserId { get; set; }

    /// <summary>
    /// The portal (tenant) the caller is signed in to, supplied from the per-request portal context rather
    /// than read from the user row.
    /// </summary>
    /// <remarks>
    /// IDENTIFIER TRAP: do not test this value for absence, and do not let a consumer do so either.
    /// <c>Portals.PortalID</c> is declared <c>[int] IDENTITY (-1, 1) NOT NULL</c> (01.00.00.SqlDataProvider
    /// L77), so the seed and first generated value is minus one, while the shipped default portal row is
    /// inserted explicitly with identifier zero under <c>IDENTITY_INSERT</c> - and zero is not
    /// hypothetical, because the baseline data seeds roles against it.
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>
    /// The display name of the portal the caller is signed in to, from <c>Portals.PortalName</c>
    /// (<c>[nvarchar] (128) NOT NULL</c>, 01.00.00.SqlDataProvider L79).
    /// </summary>
    public string PortalName { get; set; } = string.Empty;

    /// <summary>
    /// The caller's login name, from <c>Users.Username</c> (<c>nvarchar(100) NOT NULL</c>,
    /// 01.00.06.SqlDataProvider L197).
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// The caller's presentation name, from <c>Users.DisplayName</c> (<c>nvarchar(128) NOT NULL</c>
    /// defaulting to the empty string, 03.02.03.SqlDataProvider L630).
    /// </summary>
    /// <remarks>
    /// Here the empty-string encoding of "absent" is a schema constraint rather than a data-layer
    /// convention, so this member must never be surfaced as <see langword="null"/>: a caller with no
    /// display name has the empty string and the shell renders <see cref="Username"/> in its place.
    /// </remarks>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The caller's email address, from <c>Users.Email</c>.</summary>
    /// <remarks>
    /// The terminal column is <c>nvarchar(256) NULL</c>. The baseline declared it <c>[nvarchar] (100) NOT
    /// NULL</c> (01.00.00.SqlDataProvider L107); it was dropped at 02.02.01.SqlDataProvider L51 and
    /// re-added in its wider, nullable form at 03.00.13.SqlDataProvider L109-L110, which is also the width
    /// every terminal procedure parameter uses (04.00.04.SqlDataProvider L704).
    /// </remarks>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Whether the caller is a host-level super user, from <c>Users.IsSuperUser</c> (<c>bit NOT NULL</c>
    /// defaulting to zero, 01.00.02.SqlDataProvider L243, carried forward at 01.00.06.SqlDataProvider
    /// L195).
    /// </summary>
    public bool IsSuperUser { get; set; }

    /// <summary>
    /// Whether the caller administers <see cref="PortalId"/>, derived server-side from that portal's own
    /// <c>AdministratorRoleId</c> designation.
    /// </summary>
    /// <remarks>
    /// SECURITY: advisory in precisely the same sense as <see cref="Roles"/> and <see cref="Permissions"/>.
    /// It exists so a console can hide an affordance the caller cannot exercise; it unlocks nothing.
    /// </remarks>
    public bool IsPortalAdministrator { get; set; }

    /// <summary>
    /// The names of the roles the caller holds in <see cref="PortalId"/>. Never <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// WHICH RESPONSE CARRIES THIS MATTERS, and reading an empty list as "holds no roles" is wrong on two
    /// of the three. This type is returned by the current-user read, which resolves roles as of the instant
    /// it is asked, and by the sign-in and refresh responses, which deliberately do not resolve them at
    /// all.
    /// </remarks>
    public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();

    /// <summary>
    /// The permission keys the caller holds, used by the client to hide affordances it cannot exercise.
    /// Never <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Strings on the wire rather than the domain permission-key enumeration: the legacy permission keys
    /// were themselves strings evaluated by the legacy permission controllers, and a list of strings needs
    /// no converter on either side of the wire. This type offers no "may the caller do this" method,
    /// because that would place an authorisation decision inside a data carrier.
    /// </remarks>
    public IReadOnlyList<string> Permissions { get; set; } = Array.Empty<string>();

    // DELIBERATELY ABSENT, recorded so a later reader does not restore any of them believing it was
    // overlooked: any credential or session artefact (see the security note on the type); the legacy
    // profile and membership composites, which have their own contracts in the user DTO set and are fetched
    // deliberately so that this per-render response stays small; operation-status enumerations, because
    // expected failures are the service layer's outcome type rendered as an RFC 7807 payload at the API
    // edge; envelope and transport fields, which belong to the common DTO set and to request headers; and
    // the legacy property-editor and XML-serialisation attributes, because a response contract validates
    // nothing.
}
