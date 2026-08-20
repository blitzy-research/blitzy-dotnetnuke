// MIGRATION: For an unauthenticated caller the legacy accessor returned a hollow object rather than nothing
// at all, from three separate sites.

namespace DnnMigration.Application.Abstractions;

/// <summary>
/// Describes the caller on whose behalf the current request is being handled, expressed entirely as plain
/// CLR data.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> It answers exactly one question: <em>who is making this request?</em> It is the
/// counterpart to the domain layer's tenant context abstraction, which answers the different question
/// <em>which tenant is this request for?</em>, and the two must not be conflated.
/// </para>
/// <para>
/// <b>Facts, never decisions.</b> The authority-minimised bearer token reports only the account and tenant
/// identifiers. Compatibility members for names, host status, roles and permissions return their neutral
/// values and must not be used for authorization or presentation; consumers that need those facts re-read
/// them through the appropriate repository or the explicit current-user endpoint.
/// </para>
/// </remarks>
public interface ICurrentUser
{
    /// <summary>
    /// Gets a value indicating whether the current request carried credentials that were successfully
    /// verified.
    /// </summary>
    /// <value>
    /// <c>true</c> when the request presented a valid, verified token carrying parseable account and tenant
    /// identity; otherwise <c>false</c>.
    /// </value>
    /// <remarks>
    /// This is the only correct way to test for an anonymous caller; never infer anonymity by comparing an
    /// identifier against a magic number. When this property is <c>false</c>, <see cref="UserId"/> and <see
    /// cref="UserName"/> are <c>null</c> and <see cref="Roles"/> and <see cref="PermissionKeys"/> are
    /// empty.
    /// </remarks>
    bool IsAuthenticated { get; }

    /// <summary>
    /// Gets the identifier of the authenticated caller, corresponding to the legacy <c>Users.UserID</c>
    /// column.
    /// </summary>
    /// <value>The caller's user identifier, or <c>null</c> when the caller is anonymous.</value>
    /// <remarks>
    /// <c>Users.UserID</c> is declared <c>IDENTITY(1, 1)</c>, so a persisted user identifier is in practice
    /// never zero or negative - but that observation must not be re-expressed here as a validity rule. This
    /// contract reports what the request carried and performs no validation.
    /// </remarks>
    int? UserId { get; }

    /// <summary>Gets the compatibility login-name projection.</summary>
    /// <value>Always <c>null</c>.</value>
    string? UserName { get; }

    /// <summary>
    /// Gets the identifier of the portal that scopes this request, corresponding to the legacy
    /// <c>Portals.PortalID</c> column.
    /// </summary>
    /// <value>
    /// The portal identifier resolved for the request, or <c>null</c> when no portal scope has been
    /// resolved.
    /// </value>
    /// <remarks>
    /// This member exists because portal membership forms part of the caller's identity, as it did on the
    /// legacy user object. It is not a substitute for the domain layer's tenant context abstraction: read
    /// tenant configuration from that, and the caller's portal affiliation from here.
    /// </remarks>
    int? PortalId { get; }

    /// <summary>Gets the compatibility host-authority projection.</summary>
    /// <value>Always <c>false</c>.</value>
    /// <remarks>
    /// Access-control code must read the account from authoritative storage. The neutral compatibility
    /// value prevents a stale token claim from becoming an authorization shortcut.
    /// </remarks>
    bool IsSuperUser { get; }

    /// <summary>Gets the names of the security roles held by the caller.</summary>
    /// <value>Always an empty collection.</value>
    IReadOnlyList<string> Roles { get; }

    /// <summary>Gets the granular permission keys held by the caller.</summary>
    /// <value>Always an empty collection.</value>
    IReadOnlyList<string> PermissionKeys { get; }
}
