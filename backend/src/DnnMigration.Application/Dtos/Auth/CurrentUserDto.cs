namespace DnnMigration.Application.Dtos.Auth;

/// <summary>
/// The signed-in caller's own identity and interface-gating data, returned by <c>GET /api/v1/auth/me</c>:
/// the user key, the portal identifier and its display name, the caller's names and address, the super-user
/// flag, the two BLOCKING remediation obligations the session carries, and the advisory role and permission
/// lists.
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
    /// Whether the caller must replace their credential before the ordinary application surface is open to
    /// them. <see langword="false"/> means no such obligation stands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ EVERY IDENTITY HYDRATION PATH PUBLISHES THIS, AND THAT IS THE WHOLE POINT OF ITS BEING HERE. It is
    /// the same decision the sign-in and refresh responses carry on their own top-level members, evaluated
    /// from the same authoritative state through the same service member, so a client cannot receive two
    /// disagreeing descriptions of one session depending on which endpoint it asked. Its absence from this
    /// contract was measured as a defect: an obligation IMPOSED AFTER sign-in - a tenant administrator
    /// marking a profile property required while the account is signed in - reached the client on no
    /// hydration path at all, so the caller received a bare refusal on every ordinary endpoint and was
    /// never sent to the one screen that could clear it.
    /// </para>
    /// <para>
    /// SECURITY: this is not advisory in the sense <see cref="Roles"/> and <see cref="Permissions"/> are. A
    /// client that ignores it, or tampers with it, gains nothing: the pipeline stage that confines a
    /// restricted session and the authorisation handler that enforces the same rule both RE-EVALUATE the
    /// stored state on every protected request and refuse independently. What the member buys the client is
    /// the ability to state the actionable task and to land the caller on the screen that discharges it.
    /// </para>
    /// </remarks>
    public bool MustChangePassword { get; set; }

    /// <summary>
    /// Whether the caller must complete or correct their required profile properties before the ordinary
    /// application surface is open to them. <see langword="false"/> means no such obligation stands.
    /// </summary>
    /// <remarks>
    /// The blocking counterpart of <see cref="MustChangePassword"/>, and the one the legacy flow sent to a
    /// different step rather than to the credential interstitial. Both are published together because both
    /// can stand at once - the legacy post-credential enumeration was single-valued and could report only
    /// one - and a caller who has just changed their credential must be able to walk on to the profile
    /// screen without being sent backwards.
    /// </remarks>
    public bool MustUpdateProfile { get; set; }

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
    // overlooked: the NON-BLOCKING credential-expiry advisory that the sign-in and refresh responses carry,
    // because it gates nothing and the authoritative remediation value this contract mirrors - the two
    // members above - carries exactly the two obligations the API itself enforces, so publishing a third
    // would put a value here that no server-side gate re-evaluates; any credential or session artefact
    // (see the security note on the type); the legacy
    // profile and membership composites, which have their own contracts in the user DTO set and are fetched
    // deliberately so that this per-render response stays small; operation-status enumerations, because
    // expected failures are the service layer's outcome type rendered as an RFC 7807 payload at the API
    // edge; envelope and transport fields, which belong to the common DTO set and to request headers; and
    // the legacy property-editor and XML-serialisation attributes, because a response contract validates
    // nothing.
}
