// MIGRATION: The legacy caller accessor resolved identity from ambient per-request state - the
// framework's request-items collection, falling back to the ambient thread principal when no request
// was in flight. Neither ambient source is reproduced: identity is an injected, substitutable input,
// which is what makes every consuming service testable without a live request.
//
// MIGRATION: For an unauthenticated caller the legacy accessor returned a hollow object rather than
// nothing at all, from three separate sites. That empty-object sentinel is deliberately not carried
// forward - an anonymous caller is described here by IsAuthenticated being false, with null identity
// values and empty, non-null collections - which is what lets a consumer distinguish "nobody is
// signed in" from "a real principal whose identifier happens to be a low number".

namespace DnnMigration.Application.Abstractions;

/// <summary>
/// Describes the caller on whose behalf the current request is being handled, expressed entirely as
/// plain CLR data.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> It answers exactly one question: <em>who is making this request?</em> It is the
/// counterpart to the domain layer's tenant context abstraction, which answers the different question
/// <em>which tenant is this request for?</em>, and the two must not be conflated. A portal identifier
/// appears on both because portal membership is genuinely part of a caller's identity in this schema,
/// but no other tenant attribute - portal name, default language, time-zone offset, home directory or
/// active page - belongs here.
/// </para>
/// <para>
/// <b>No numeric value on this surface may carry sentinel meaning.</b> The legacy constructor
/// initialised both the user and portal identifiers from the shared integer sentinel, whose value is
/// -1; but <c>Portals.PortalID</c> is declared <c>IDENTITY(-1, 1)</c>, so -1 is simultaneously the
/// legacy "absent" marker and the seed of the portal identity column - whose shipped default row carries
/// an explicit 0 - and the role, page and module
/// keys all seed at 0. Absence is therefore carried exclusively by the nullable types below and by
/// <see cref="IsAuthenticated"/>. An implementation must never coalesce -1 or 0 to
/// <see langword="null"/>, and a consumer must never read either value as meaning "anonymous". An
/// absent string is likewise <see langword="null"/> and never the empty string, which is what the
/// legacy string sentinel was.
/// </para>
/// <para>
/// <b>Facts, never decisions.</b> Every member reports something already true about the request.
/// Deliberately absent are helpers that would answer "may the caller do X?": that is an access-control
/// decision, settled by the API layer's authorisation policies over the infrastructure permission
/// evaluator. Exposing a decision helper here would let a controller settle a business question
/// directly. Consumers read <see cref="Roles"/> and <see cref="PermissionKeys"/> and let the policy
/// layer adjudicate.
/// </para>
/// <para>
/// <b>Deliberately free of transport types, and deliberately synchronous.</b> Nothing from the HTTP
/// stack, and no type describing the authentication mechanism itself, appears here; this project
/// declares one project reference, to the domain layer, so reaching for a transport type does not
/// compile. The caller's identity is already materialised before a request reaches the application
/// layer, so reading it performs no I/O. An implementation must never perform I/O - nor block,
/// cache-fill or log - inside a getter. The legacy roles accessor shows the failure mode this closes
/// off: its getter silently issued a database query to hydrate itself on first read.
/// </para>
/// <para>
/// <b>Registration.</b> No implementation can exist in a project that cannot see the request, so the
/// API layer must register this abstraction, with a <em>scoped</em> (per-request) lifetime, projecting
/// the values below from the verified claims of the authenticated principal. A singleton registration
/// would capture one caller's identity and serve it to every subsequent request.
/// </para>
/// <para>
/// <b>Implementer's obligations.</b> Return the values described on each member exactly, including
/// the stated behaviour for an anonymous caller. Never substitute an empty string for an absent
/// string, never return a null collection, never treat -1 or 0 as absence, and never perform I/O in a
/// getter. These members are read on nearly every request, so each should be a cheap field or
/// lazily-projected-once read over data already in memory.
/// </para>
/// </remarks>
public interface ICurrentUser
{
    /// <summary>
    /// Gets a value indicating whether the current request carried credentials that were successfully
    /// verified.
    /// </summary>
    /// <value>
    /// <c>true</c> when the request presented a valid, verified token and the caller's identity was
    /// established; otherwise <c>false</c>.
    /// </value>
    /// <remarks>
    /// This is the only correct way to test for an anonymous caller; never infer anonymity by
    /// comparing an identifier against a magic number. When this property is <c>false</c>,
    /// <see cref="UserId"/> and <see cref="UserName"/> are <c>null</c> and <see cref="Roles"/> and
    /// <see cref="PermissionKeys"/> are empty.
    /// </remarks>
    bool IsAuthenticated { get; }

    /// <summary>
    /// Gets the identifier of the authenticated caller, corresponding to the legacy
    /// <c>Users.UserID</c> column.
    /// </summary>
    /// <value>
    /// The caller's user identifier, or <c>null</c> when the caller is anonymous.
    /// </value>
    /// <remarks>
    /// <c>Users.UserID</c> is declared <c>IDENTITY(1, 1)</c>, so a persisted user identifier is in
    /// practice never zero or negative - but that observation must not be re-expressed here as a
    /// validity rule. This contract reports what the request carried and performs no validation.
    /// </remarks>
    int? UserId { get; }

    /// <summary>
    /// Gets the login name of the authenticated caller, corresponding to the legacy
    /// <c>Users.Username</c> column.
    /// </summary>
    /// <value>
    /// The caller's user name, or <c>null</c> when the caller is anonymous.
    /// </value>
    /// <remarks>
    /// An absent user name is <c>null</c> - never the empty string, which would revive the legacy
    /// string sentinel and make "no caller" indistinguishable from "a caller whose name failed to
    /// project". An implementation should treat a blank or whitespace-only claim value as absent.
    /// </remarks>
    string? UserName { get; }

    /// <summary>
    /// Gets the identifier of the portal that scopes this request, corresponding to the legacy
    /// <c>Portals.PortalID</c> column.
    /// </summary>
    /// <value>
    /// The portal identifier resolved for the request, or <c>null</c> when no portal scope has been
    /// resolved. Only <c>null</c> means unresolved.
    /// </value>
    /// <remarks>
    /// This member exists because portal membership forms part of the caller's identity, as it did on
    /// the legacy user object. It is not a substitute for the domain layer's tenant context
    /// abstraction: read tenant configuration from that, and the caller's portal affiliation from
    /// here. Where a request is scoped to a tenant the caller does not belong to, resolving that
    /// discrepancy is an access-control decision for the policy layer.
    /// </remarks>
    int? PortalId { get; }

    /// <summary>
    /// Gets a value indicating whether the caller is flagged as a host-level super user,
    /// corresponding to the legacy <c>Users.IsSuperUser</c> column.
    /// </summary>
    /// <value>
    /// <c>true</c> when the caller's verified identity carries the super-user flag; otherwise
    /// <c>false</c>. Always <c>false</c> for an anonymous caller.
    /// </value>
    /// <remarks>
    /// <b>Informational only, and never an access-control shortcut.</b> The legacy role-check helper
    /// opened by short-circuiting on this flag, which let a single boolean act as a blanket permission
    /// grant across the whole application; that shortcut is not reproduced, and a guard written
    /// against this flag would silently re-create it. Access control is policy-based and evaluated in
    /// the API layer. Host-level super-user administration is outside the migrated feature set, so the
    /// flag is surfaced for auditing, diagnostics and presentation only.
    /// </remarks>
    bool IsSuperUser { get; }

    /// <summary>
    /// Gets the names of the security roles held by the caller.
    /// </summary>
    /// <value>
    /// The caller's role names, or an empty collection when the caller is anonymous or holds no roles.
    /// Never <c>null</c>.
    /// </value>
    /// <remarks>
    /// These derive from the legacy <c>Roles</c>, <c>UserRoles</c> and <c>RoleGroups</c> tables,
    /// projected into the caller's verified claims by the API layer. An implementation must return an
    /// empty collection rather than <c>null</c>, which would force a defensive check at every use site
    /// and invite a null-reference fault on the common path. The collection is read-only because the
    /// caller's roles are a fact about the request that a consumer has no business mutating - the
    /// legacy equivalent was a mutable array whose getter also lazily queried the database, and both
    /// of those properties are deliberately gone.
    /// </remarks>
    IReadOnlyList<string> Roles { get; }

    /// <summary>
    /// Gets the granular permission keys held by the caller.
    /// </summary>
    /// <value>
    /// The caller's permission keys, or an empty collection when the caller is anonymous or holds
    /// none. Never <c>null</c>.
    /// </value>
    /// <remarks>
    /// These mirror the permission keys the API layer evaluates server-side, so a client and the
    /// server reason over the same vocabulary; reporting them supports presentation - deciding whether
    /// to render an action a caller could not perform - and diagnostics. Exposing the collection is
    /// not the same as granting anything: whether a given operation is allowed remains a decision for
    /// the API layer's authorisation policies, which are the sole enforcement point, and inspection of
    /// this collection must never become the only check protecting an operation.
    /// </remarks>
    IReadOnlyList<string> PermissionKeys { get; }
}
