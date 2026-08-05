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
/// <b>Facts, never decisions.</b> The authority-minimised bearer token reports only the account and
/// tenant identifiers. Compatibility members for names, host status, roles and permissions return
/// their neutral values and must not be used for authorization or presentation; consumers that need
/// those facts re-read them through the appropriate repository or the explicit current-user
/// endpoint.
/// Deliberately absent are helpers that would answer "may the caller do X?": that is an access-control
/// decision, settled by the API layer's authorisation policies over the infrastructure permission
/// evaluator. Exposing a decision helper here would let a controller settle a business question
/// directly.
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
    /// <c>true</c> when the request presented a valid, verified token carrying parseable account and
    /// tenant identity; otherwise <c>false</c>.
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
    /// Gets the compatibility login-name projection.
    /// </summary>
    /// <value>
    /// Always <c>null</c>. User names are mutable profile data and are not carried in access tokens.
    /// </value>
    /// <remarks>
    /// Consumers that need a display or audit name must read it from authoritative account storage;
    /// the identifier remains available through <see cref="UserId"/>.
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
    /// Gets the compatibility host-authority projection.
    /// </summary>
    /// <value>
    /// Always <c>false</c>. Host authority is mutable and is not carried in access tokens.
    /// </value>
    /// <remarks>
    /// Access-control code must read the account from authoritative storage. The neutral compatibility
    /// value prevents a stale token claim from becoming an authorization shortcut.
    /// </remarks>
    bool IsSuperUser { get; }

    /// <summary>
    /// Gets the names of the security roles held by the caller.
    /// </summary>
    /// <value>
    /// Always an empty collection. Roles are mutable authority and are not carried in access tokens.
    /// </value>
    /// <remarks>
    /// Consumers that need roles must read them from authoritative storage or use the store-backed
    /// <c>/auth/me</c> projection. Never <c>null</c>.
    /// </remarks>
    IReadOnlyList<string> Roles { get; }

    /// <summary>
    /// Gets the granular permission keys held by the caller.
    /// </summary>
    /// <value>
    /// Always an empty collection. Permission keys are mutable authority and are not carried in
    /// access tokens.
    /// </value>
    /// <remarks>
    /// The API layer evaluates permissions from authoritative storage on every protected request, and
    /// the current-user endpoint supplies the client-side affordance projection. Never <c>null</c>.
    /// </remarks>
    IReadOnlyList<string> PermissionKeys { get; }
}
