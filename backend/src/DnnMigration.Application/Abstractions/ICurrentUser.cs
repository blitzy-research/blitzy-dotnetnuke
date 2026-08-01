// MIGRATION: This contract replaces the ambient static accessor
// UserController.GetCurrentUserInfo(), at Library/Components/Users/UserController.vb:L381.
// The legacy member resolved the caller from ambient per-request state — the framework's
// request-items collection, keyed by the literal string "UserInfo" (L396) — and, when no
// request was in flight (L383), fell back to the ambient thread principal's identity (L384).
// Neither ambient source is reproduced. The caller's identity is now supplied by dependency
// injection, making it an explicit, substitutable input instead of hidden global state, and
// making every consuming service testable without a live request. This follows the technique
// prescribed by AAP 0.7.4: a member that was Shared (static) becomes an instance member on an
// injected abstraction.
//
// MIGRATION: For an unauthenticated caller the legacy accessor returned `New UserInfo` — a
// hollow object, never Nothing — from three separate sites (L385, L392, L400). That
// empty-object sentinel is deliberately NOT carried forward. An anonymous caller is described
// here by IsAuthenticated being false, with null identity values and empty, non-null
// collections. This is a documented behavioural difference under AAP Rule T5, not an
// oversight, and it is what allows a consumer to distinguish "nobody is signed in" from "a
// real principal whose identifier happens to be a low number".
//
// MIGRATION: The measurement below is why the hollow object had to go, and why no numeric
// value on this contract may ever carry sentinel meaning. The legacy constructor initialised
// both the user and portal identifiers from the shared integer sentinel
// (Library/Components/Users/UserInfo.vb:L66-L67), whose value is -1
// (Library/Components/Shared/Null.vb:L41-L45). The schema, however, declares
// Portals.PortalID as IDENTITY(-1, 1) at
// Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L77 — so -1 is
// simultaneously the legacy "absent" marker and the identifier of the first real portal.
// Roles.RoleID (L115), Tabs.TabID (L140) and Modules.ModuleID (L221) all seed at 0, making 0
// a legitimate identifier too. Absence is therefore carried exclusively by the nullable types
// on this surface, per AAP Rule T7. An implementation must never coalesce -1 or 0 to null, and
// a consumer must never read either value as meaning "anonymous".
//
// MIGRATION: Members here are deliberately synchronous, which is a design decision rather than
// an omission. The caller's identity is already materialised before a request reaches the
// application layer, so reading it performs no I/O, and AAP Rule T6 governs I/O-bound work
// only. The legacy roles accessor (Library/Components/Users/UserInfo.vb:L261-L269) shows the
// exact failure mode this closes off: its getter silently issued a database query to hydrate
// itself on first read. An implementation of this contract must never perform I/O — nor block,
// cache-fill or log — inside a property getter. If a value were not already known, the
// identity would not yet be resolved, and that would be an architectural fault upstream.

namespace DnnMigration.Application.Abstractions;

/// <summary>
/// Describes the caller on whose behalf the current request is being handled, expressed
/// entirely as plain CLR data.
/// </summary>
/// <remarks>
/// <para>
/// Scope of this contract. It answers exactly one question: <em>who is making this
/// request?</em> It is the counterpart to the tenant context abstraction owned by the domain
/// layer, which answers the different question <em>which tenant is this request for?</em> The
/// two are intentionally separate and must not be conflated. A portal identifier appears on
/// both because portal membership is genuinely part of a caller's identity in this schema, but
/// no other tenant attribute — portal name, default language, time-zone offset, home
/// directory or active page — belongs here. Read those from the tenant context instead.
/// </para>
/// <para>
/// Deliberately free of transport types. Nothing from the HTTP stack, and no type describing
/// the authentication mechanism itself, appears on this surface. Identity is presented as
/// primitives and read-only string collections so that an application service reasons about
/// <em>who the caller is</em> rather than about how the caller was authenticated. AAP 0.7.5.1
/// confines request-context access to a single middleware component in the API layer; this
/// project reinforces that structurally, declaring one project reference (the domain layer)
/// and no web framework reference at all, so reaching for a transport type here does not
/// compile. That is the design, not an inconvenience.
/// </para>
/// <para>
/// Facts, never decisions. Every member reports something that is already true about the
/// request. Deliberately absent are helpers that would answer "may the caller do X?", because
/// that is an access-control decision, and AAP 0.4.3 assigns those to the API layer's
/// permission policy handler and to the infrastructure permission evaluator. Exposing a
/// decision helper here would let a controller settle a business question directly, which
/// AAP Rule T2 forbids. Consumers read <see cref="Roles"/> and <see cref="PermissionKeys"/>
/// and let the policy layer adjudicate.
/// </para>
/// <para>
/// Registration and lifetime. This abstraction is intentionally NOT registered by the
/// application layer's <c>AddApplication()</c> extension, which registers exactly seven
/// services — <c>IPortalService</c>, <c>IModuleService</c>, <c>IUserService</c>,
/// <c>IRoleService</c>, <c>IPermissionService</c>, <c>ITabService</c> and
/// <c>IAuthService</c> — and excludes this one by design, because no implementation of it can
/// exist in a project that cannot see the request. The API layer registers it instead, with a
/// scoped (per-request) lifetime, projecting the values below from the verified claims of the
/// authenticated principal. A singleton registration would be incorrect: it would capture one
/// caller's identity and serve it to every subsequent request. Consuming application services
/// are themselves scoped, so a scoped implementation composes correctly with them.
/// </para>
/// <para>
/// Implementer's checklist. Return the values described on each member exactly, including the
/// stated behaviour for an anonymous caller. Never substitute an empty string for an absent
/// string, never return a null collection, never treat -1 or 0 as absence, and never perform
/// I/O in a getter. These members are read on nearly every request, so each should be a cheap
/// field or lazily-projected-once read over data already in memory.
/// </para>
/// </remarks>
public interface ICurrentUser
{
    /// <summary>
    /// Gets a value indicating whether the current request carried credentials that were
    /// successfully verified.
    /// </summary>
    /// <value>
    /// <c>true</c> when the request presented a valid, verified token and the caller's
    /// identity was established; otherwise <c>false</c>.
    /// </value>
    /// <remarks>
    /// This is the only correct way to test for an anonymous caller. Never infer anonymity by
    /// comparing an identifier against a magic number: as recorded in the migration notes at
    /// the head of this file, both -1 and 0 are real identifiers in this schema. When this
    /// property is <c>false</c>, <see cref="UserId"/> and <see cref="UserName"/> are
    /// <c>null</c> and <see cref="Roles"/> and <see cref="PermissionKeys"/> are empty.
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
    /// <c>Users.UserID</c> is declared IDENTITY(1, 1), so a persisted user identifier is in
    /// practice never zero or negative. That observation must not be re-expressed here as a
    /// validity rule: this contract reports what the request carried and performs no
    /// validation. Absence is signalled solely by <c>null</c>, and a value of 0 or -1, however
    /// unlikely for this particular column, is data rather than a sentinel.
    /// </remarks>
    int? UserId { get; }

    // MIGRATION: The legacy string sentinel was the empty string rather than null
    // (Library/Components/Shared/Null.vb:L71-L75), so a database NULL and a genuinely empty
    // value were indistinguishable once read through the legacy hydration path. This contract
    // deliberately uses null for an absent string, and an implementation must not substitute
    // an empty string to emulate the old behaviour.
    /// <summary>
    /// Gets the login name of the authenticated caller, corresponding to the legacy
    /// <c>Users.Username</c> column.
    /// </summary>
    /// <value>
    /// The caller's user name, or <c>null</c> when the caller is anonymous.
    /// </value>
    /// <remarks>
    /// An absent user name is <c>null</c> — never the empty string. Returning an empty string
    /// would revive the legacy sentinel described immediately above and would make "no caller"
    /// indistinguishable from "a caller whose name failed to project". Implementations should
    /// treat a blank or whitespace-only claim value as absent and return <c>null</c>.
    /// </remarks>
    string? UserName { get; }

    /// <summary>
    /// Gets the identifier of the portal that scopes this request, corresponding to the legacy
    /// <c>Portals.PortalID</c> column.
    /// </summary>
    /// <value>
    /// The portal identifier resolved for the request, or <c>null</c> when no portal scope has
    /// been resolved.
    /// </value>
    /// <remarks>
    /// <para>
    /// Both -1 and 0 are valid, real portal identifiers, because <c>Portals.PortalID</c> is
    /// declared IDENTITY(-1, 1) — the first portal created in a legacy installation has the
    /// identifier -1. Neither value may be interpreted as "absent" or "anonymous", and neither
    /// may be coalesced to <c>null</c>. Only <c>null</c> means unresolved.
    /// </para>
    /// <para>
    /// This member exists because portal membership forms part of the caller's identity, as it
    /// did on the legacy user object. It is not a substitute for the domain layer's tenant
    /// context abstraction: read tenant configuration from that, and read the caller's portal
    /// affiliation from here. Where a request is scoped to a tenant the caller does not belong
    /// to, resolving that discrepancy is an access-control decision for the policy layer, not
    /// something this contract reports on.
    /// </para>
    /// </remarks>
    int? PortalId { get; }

    // MIGRATION: This flag is reported as information only. The legacy role-check helper at
    // Library/Components/Users/UserInfo.vb:L322 opened by short-circuiting on the super-user
    // flag, which let a single boolean act as a blanket permission grant across the whole
    // application. Host-level super-user administration is excluded from this migration's
    // scope per AAP 0.2.2.4, so that shortcut is not reproduced and this flag must never
    // settle an access-control question.
    /// <summary>
    /// Gets a value indicating whether the caller is flagged as a host-level super user,
    /// corresponding to the legacy <c>Users.IsSuperUser</c> column.
    /// </summary>
    /// <value>
    /// <c>true</c> when the caller's verified identity carries the super-user flag; otherwise
    /// <c>false</c>. Always <c>false</c> for an anonymous caller.
    /// </value>
    /// <remarks>
    /// Informational only, and never an access-control shortcut. It is surfaced so that
    /// auditing, diagnostics and presentation can reflect the caller's standing, not so that a
    /// service can bypass a permission check. Access control is policy-based and evaluated in
    /// the API layer; a guard written against this flag would silently re-create the legacy
    /// blanket grant described immediately above. Host-level administration is not part of the
    /// migrated feature set.
    /// </remarks>
    bool IsSuperUser { get; }

    /// <summary>
    /// Gets the names of the security roles held by the caller.
    /// </summary>
    /// <value>
    /// The caller's role names, or an empty collection when the caller is anonymous or holds
    /// no roles. Never <c>null</c>.
    /// </value>
    /// <remarks>
    /// <para>
    /// Per AAP 0.4.3 these derive from the legacy <c>Roles</c>, <c>UserRoles</c> and
    /// <c>RoleGroups</c> tables, projected into the caller's verified claims by the API layer.
    /// </para>
    /// <para>
    /// Implementations must return an empty collection rather than <c>null</c> when the caller
    /// has no roles. Returning <c>null</c> would force a defensive check at every use site and
    /// invite a null-reference fault on the common path. The collection is read-only because
    /// the caller's roles are a fact about the request that a consumer has no business
    /// mutating; the legacy equivalent was a mutable array whose getter also lazily queried the
    /// database, and both of those properties are deliberately gone.
    /// </para>
    /// </remarks>
    IReadOnlyList<string> Roles { get; }

    /// <summary>
    /// Gets the granular permission keys held by the caller.
    /// </summary>
    /// <value>
    /// The caller's permission keys, or an empty collection when the caller is anonymous or
    /// holds none. Never <c>null</c>.
    /// </value>
    /// <remarks>
    /// <para>
    /// These mirror the permission keys the API layer evaluates server-side, so a client and
    /// the server reason over the same vocabulary. Reporting them here supports presentation
    /// concerns — deciding whether to render an action a caller could not perform — and
    /// diagnostics.
    /// </para>
    /// <para>
    /// Exposing the collection is not the same as granting anything. This contract states
    /// which keys the caller holds; whether a given operation is allowed remains a decision
    /// for the API layer's permission policy handler, which is the sole enforcement point.
    /// Client-side or service-side inspection of this collection must never become the only
    /// check protecting an operation. As with <see cref="Roles"/>, an empty collection —
    /// never <c>null</c> — is returned when the caller holds no keys.
    /// </para>
    /// </remarks>
    IReadOnlyList<string> PermissionKeys { get; }
}
