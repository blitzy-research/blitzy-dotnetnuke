
namespace DnnMigration.Application.Dtos.Auth;

/// <summary>
/// The signed-in caller's own identity and interface-gating data, returned by
/// <c>GET /api/v1/auth/me</c>: the user key, the portal identifier and its
/// display name, the caller's names and address, the super-user flag, and the
/// advisory role and permission lists.
/// </summary>
/// <remarks>
/// <para>
/// SECURITY: no credential material and no session artefact appear on this
/// contract, and none may ever be added. This body is emitted on every
/// application-shell render, which makes it the payload most likely to be
/// captured by request and response logging; keeping every credential-shaped
/// member off it is what makes that logging safe by construction.
/// </para>
/// <para>
/// Both portal members are supplied from the portal context the request
/// pipeline resolves, never read from the user row: the <c>Users</c> table has
/// no portal column (<c>CREATE TABLE [dbo].[Users]</c>,
/// 01.00.00.SqlDataProvider L97-L111, and no script in the 88-script upgrade
/// chain adds one) because per-portal membership is a table of its own,
/// <c>UserPortals(UserId, PortalId, Authorized)</c> at L153-L156.
/// </para>
/// <para>
/// SECURITY: <see cref="Roles"/> and <see cref="Permissions"/> are advisory
/// only - they exist so the client can hide affordances the caller cannot
/// exercise. Authoritative enforcement is server-side policy-based
/// authorisation, so a caller who tampers with this response changes what a
/// menu looks like and nothing about what the API will permit.
/// </para>
/// <para>
/// SENTINEL BOUNDARY: the legacy data layer encoded "absent" as a per-type
/// sentinel rather than SQL <c>NULL</c> - minus one for integers, the empty
/// string for text, <see langword="false"/> for booleans - and its null test
/// reported <see langword="true"/> for all three (<c>Null.vb</c> L36-L85,
/// L208-L237). Every string member here is therefore non-nullable and defaults
/// to the empty string, and <see cref="IsSuperUser"/> is a plain <c>bool</c>: a
/// nullable form would advertise a distinction the source data cannot make. No
/// date member is carried at all, so no date sentinel decision arises.
/// </para>
/// <para>
/// Serialisation, as actually configured, cannot weaken this contract. The host
/// sets the <see cref="System.Text.Json.Serialization.JsonIgnoreCondition.Never"/>
/// ignore condition (<c>ServiceCollectionExtensions.cs</c>, stated on both the
/// minimal-API and controller surfaces), which omits nothing at all - so a legitimate
/// <see langword="false"/>, a legitimate empty string and an empty collection all
/// reach the wire, and so would a null if any member here were nullable. A
/// "when writing null" or "when writing default" condition would weaken it: the
/// latter erases exactly those three, and both must stay unconfigured, because either
/// turns a real value into an absent field and silently changes the contract clients
/// bind to.
/// </para>
/// </remarks>
public sealed class CurrentUserDto
{

    /// <summary>
    /// The surrogate key of the signed-in user, from <c>Users.UserID</c>,
    /// declared <c>[int] IDENTITY (1, 1) NOT NULL</c>
    /// (01.00.00.SqlDataProvider L98).
    /// </summary>
    /// <remarks>
    /// The legacy column name is retained rather than generalised to <c>Id</c>,
    /// so every identifier on every contract lines up against the schema
    /// without a translation table.
    /// </remarks>
    public int UserId { get; set; }

    /// <summary>
    /// The portal (tenant) the caller is signed in to, supplied from the
    /// per-request portal context rather than read from the user row.
    /// </summary>
    /// <remarks>
    /// IDENTIFIER TRAP: do not test this value for absence, and do not let a
    /// consumer do so either. <c>Portals.PortalID</c> is declared
    /// <c>[int] IDENTITY (-1, 1) NOT NULL</c> (01.00.00.SqlDataProvider L77),
    /// so the seed and first generated value is minus one, while the shipped
    /// default portal row is inserted explicitly with identifier zero under
    /// <c>IDENTITY_INSERT</c> (L7125) - and zero is not hypothetical, because the
    /// baseline data seeds roles against it (L7192, L7194). Both are valid keys.
    /// Minus one is simultaneously the legacy encoding for a missing integer, so a
    /// single value means both a real portal and "no portal at all". A guard that
    /// rejects a non-positive identifier therefore rejects two real tenants.
    /// Role, tab and module keys are seeded from zero for the same reason
    /// (L115, L140, L221).
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>
    /// The display name of the portal the caller is signed in to, from
    /// <c>Portals.PortalName</c> (<c>[nvarchar] (128) NOT NULL</c>,
    /// 01.00.00.SqlDataProvider L79).
    /// </summary>
    /// <remarks>
    /// Carried so the shell header can title itself without a second round
    /// trip. Like <see cref="PortalId"/> it comes from the resolved portal
    /// context, not from the user row.
    /// </remarks>
    public string PortalName { get; set; } = string.Empty;

    /// <summary>
    /// The caller's login name, from <c>Users.Username</c>
    /// (<c>nvarchar(100) NOT NULL</c>, 01.00.06.SqlDataProvider L197).
    /// </summary>
    /// <remarks>
    /// The only uniquely constrained attribute on the user row, so this member -
    /// and not <see cref="Email"/> - is the account key.
    /// </remarks>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// The caller's presentation name, from <c>Users.DisplayName</c>
    /// (<c>nvarchar(128) NOT NULL</c> defaulting to the empty string,
    /// 03.02.03.SqlDataProvider L630).
    /// </summary>
    /// <remarks>
    /// Here the empty-string encoding of "absent" is a schema constraint rather
    /// than a data-layer convention, so this member must never be surfaced as
    /// <see langword="null"/>: a caller with no display name has the empty
    /// string and the shell renders <see cref="Username"/> in its place. The
    /// given and family names are not carried - the user detail contract owns
    /// them, and the legacy combined-name property computed itself inside its
    /// own getter, which a data carrier does not do.
    /// </remarks>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// The caller's email address, from <c>Users.Email</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The terminal column is <c>nvarchar(256) NULL</c>. The baseline declared
    /// it <c>[nvarchar] (100) NOT NULL</c> (01.00.00.SqlDataProvider L107); it
    /// was dropped at 02.02.01.SqlDataProvider L51 and re-added in its wider,
    /// nullable form at 03.00.13.SqlDataProvider L109-L110, which is also the
    /// width every terminal procedure parameter uses
    /// (04.00.04.SqlDataProvider L704). This member is nevertheless
    /// non-nullable and defaults to the empty string, because the boundary
    /// carries the legacy absent-text encoding rather than the column's
    /// nullability: a caller with no recorded address receives the empty
    /// string.
    /// </para>
    /// <para>
    /// NOT AN ACCOUNT KEY: the column carries no unique constraint, and the
    /// legacy membership provider was registered without requiring unique
    /// addresses, so two accounts may legitimately share one. Nothing
    /// downstream may treat this member as an identifier or as a sign-in
    /// credential.
    /// </para>
    /// </remarks>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Whether the caller is a host-level super user, from
    /// <c>Users.IsSuperUser</c> (<c>bit NOT NULL</c> defaulting to zero,
    /// 01.00.02.SqlDataProvider L243, carried forward at
    /// 01.00.06.SqlDataProvider L195).
    /// </summary>
    /// <remarks>
    /// Host-level administration is beyond the scope of this migration, so the
    /// flag exists to let the shell suppress affordances rather than to unlock
    /// any; like the role and permission lists it is advisory and the server
    /// decides.
    /// </remarks>
    public bool IsSuperUser { get; set; }

    /// <summary>
    /// The names of the roles the caller holds in <see cref="PortalId"/>. Never
    /// <see langword="null"/>. An empty list means the caller holds none ONLY on
    /// the current-user read; on the sign-in and refresh responses it is always
    /// empty by design and reports nothing at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHICH RESPONSE CARRIES THIS MATTERS, and reading an empty list as "holds
    /// no roles" is wrong on two of the three. This type is returned by the
    /// current-user read, which resolves roles as of the instant it is asked, and
    /// by the sign-in and refresh responses, which deliberately do not resolve
    /// them at all. Authority handed out at a credential exchange is a snapshot
    /// that stops being true the moment an assignment is withdrawn, and a client
    /// holding one has every reason to trust it for the life of the session - so
    /// those two responses omit it rather than serve it stale, and the endpoint
    /// that does serve it is the one whose answer is fresh by construction.
    /// </para>
    /// <para>
    /// Advisory in every case. The list exists so a client can hide affordances
    /// it cannot exercise; it is never the basis of an access decision, because
    /// the authorisation policies re-read the caller's roles per request and are
    /// the only thing that grants anything.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy getter performed database access. Measured at
    /// <c>UserInfo.vb</c> L262-L269, it tested a private hydration flag and,
    /// when that flag was unset, constructed a role controller and queried the
    /// roles for the user and portal from inside the property accessor - so
    /// merely serialising the object issued a query. On a response fetched once
    /// per shell render that is the wrong place for I/O, so both the lazy getter
    /// and its flag are dropped and the service fills this list from data it has
    /// already loaded. Role names only: the role DTO set owns the richer shape.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();

    /// <summary>
    /// The permission keys the caller holds, used by the client to hide
    /// affordances it cannot exercise. Never <see langword="null"/>. An empty
    /// list means the caller holds none ONLY on the current-user read; on the
    /// sign-in and refresh responses it is always empty by design and reports
    /// nothing at all - see the note on <see cref="Roles"/>.
    /// </summary>
    /// <remarks>
    /// Strings on the wire rather than the domain permission-key enumeration:
    /// the legacy permission keys were themselves strings evaluated by the
    /// legacy permission controllers, and a list of strings needs no converter
    /// on either side of the wire. This type offers no "may the caller do this"
    /// method, because that would place an authorisation decision inside a data
    /// carrier.
    /// </remarks>
    public IReadOnlyList<string> Permissions { get; set; } = Array.Empty<string>();

    // DELIBERATELY ABSENT, recorded so a later reader does not restore any of
    // them believing it was overlooked: any credential or session artefact (see
    // the security note on the type); the legacy profile and membership
    // composites, which have their own contracts in the user DTO set and are
    // fetched deliberately so that this per-render response stays small;
    // operation-status enumerations, because expected failures are the service
    // layer's outcome type rendered as an RFC 7807 payload at the API edge;
    // envelope and transport fields, which belong to the common DTO set and to
    // request headers; and the legacy property-editor and XML-serialisation
    // attributes, because a response contract validates nothing.
}
