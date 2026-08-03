using Microsoft.AspNetCore.Authorization;

namespace DnnMigration.Api.Authorization;

/// <summary>
/// Requires that the caller administers the portal that the current request resolved to.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS TYPE EXISTS AT ALL, WHEN THE FRAMEWORK ALREADY SHIPS A ROLE REQUIREMENT. Because the
/// framework's role requirement compares a role NAME against the caller's claims and has no concept of a
/// tenant, and in this schema administrator role identity is per-tenant. Three facts about the legacy
/// database combine into the defect:
/// </para>
/// <para>
/// First, each portal names its own administrator role: <c>Portals.AdministratorRoleId</c> is a real
/// nullable column (<c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c>
/// line 91), so the identity of "the administrator role" is a per-row fact rather than a per-installation
/// one. Second, <c>Roles.PortalID</c> is itself nullable and every portal holds its own role rows, so two
/// portals routinely hold two DISTINCT administrator roles with two distinct keys. Third, and decisively,
/// no unique constraint or unique index on <c>Roles.RoleName</c> appears anywhere in the eighty-eight
/// upgrade scripts, so those two distinct rows are free to carry the SAME name - and in a stock
/// installation they both carry "Administrators".
/// </para>
/// <para>
/// The consequence is that a name-based test cannot distinguish them. An administrator of portal A holds
/// a claim reading "Administrators" and so satisfies a name-based check performed while serving portal B.
/// That is a cross-tenant privilege escalation reached without any attack: it is simply what a bare role
/// name means in a multi-tenant schema whose role names are not unique. A requirement that carries no
/// data and is decided against the resolved tenant, which is what this is, is the fix.
/// </para>
/// <para>
/// WHY THIS REQUIREMENT CARRIES NO DATA. There is deliberately no role name, no role key and no portal key
/// on it. Every one of those is a per-request fact, and a requirement instance is built once when the
/// policy is registered and then shared by every request that uses it. Putting a tenant fact on it would
/// therefore freeze the first tenant's value into a shared object - the same class of defect as the bare
/// role name, arrived at from the other direction. The values come from the resolved tenant context at
/// decision time, and the handler is where that happens.
/// </para>
/// <para>
/// WHY IT IS NOT A PERMISSION REQUIREMENT. Portal administration is membership of a role, not the
/// presence of a permission triad entry, and the legacy surface draws exactly that line: its three
/// permission controllers expose tab-scoped and module-scoped evaluation only, with no site-wide
/// equivalent, while the administration screens gate on role membership directly. The two are kept as two
/// requirement types so that neither evaluator has to carry a special case for the other's question.
/// </para>
/// </remarks>
internal sealed class PortalAdministratorRequirement : IAuthorizationRequirement
{
    /// <summary>
    /// The single shared instance.
    /// </summary>
    /// <remarks>
    /// Safe to share precisely because the type carries no state; offered as a member so that a
    /// registration site cannot be tempted to construct a per-tenant variant.
    /// </remarks>
    public static PortalAdministratorRequirement Instance { get; } = new();
}

/// <summary>
/// Requires that the caller is a host account, for the operations that address no single portal.
/// </summary>
/// <remarks>
/// <para>
/// WHY THE PORTAL REQUIREMENT CANNOT SERVE THESE OPERATIONS. A handful of endpoints carry no portal binding
/// at all - the portal collection, portal creation, and the alias resources addressed by their own global
/// identifier. The portal-administrator requirement, finding no portal in the route, falls back to the
/// tenant the caller arrived through and asks whether they administer THAT. For a global operation the
/// answer is truthful and irrelevant: an administrator of one tenant would satisfy it and then enumerate
/// every tenant, create new ones, or read and delete another tenant's alias by guessing its identifier.
/// Those operations are host-scoped by nature, so they need a host-scoped requirement.
/// </para>
/// <para>
/// WHY THIS IS NOT THE EXCLUDED HOST-ADMINISTRATION FEATURE. What the migration excludes is the super-user
/// CONSOLE and the screens reachable only from it. The concept is already present and already load-bearing:
/// <c>Users.IsSuperUser</c> is a mapped column, the token service emits it as a claim, the current-user
/// abstraction exposes it, and both the permission service and the portal service already branch on it -
/// the latter to decide who may alter a portal's hosting charge and quotas. This requirement consumes what
/// exists rather than adding a feature.
/// </para>
/// <para>
/// Carries no data, for the same reason its sibling does not: a requirement instance is shared by every
/// request that uses the policy, so a per-request fact stored on one would be frozen at the first request.
/// </para>
/// </remarks>
internal sealed class HostAdministratorRequirement : IAuthorizationRequirement
{
    /// <summary>The single shared instance.</summary>
    public static HostAdministratorRequirement Instance { get; } = new();
}

/// <summary>
/// Requires that the caller is the account the route names, and optionally admits an administrator of the
/// portal the route names as well.
/// </summary>
/// <remarks>
/// <para>
/// WHY THE ACCOUNT FAMILY NEEDS ITS OWN REQUIREMENT. Every other resource in this API has one legitimate
/// class of caller. The account resources have two: the holder, reaching their own profile or changing their
/// own credential, and an administrator of the account's portal acting on their behalf. Authentication alone
/// - which is what these endpoints previously required - let any bearer token name any portal and any
/// account in the route and read that account's personal data or overwrite its credential. Portal
/// administration alone would delete self-service.
/// </para>
/// <para>
/// WHY THE ADMINISTRATOR ARM IS A FLAG RATHER THAN A SECOND TYPE. The two policies differ in exactly one
/// bit, and the alternative - two requirement types with two handlers - would duplicate the ownership test
/// so that the two could drift apart on the one comparison that matters. The flag is set at registration,
/// never per request, so the instances stay shareable.
/// </para>
/// <para>
/// WHY THE CREDENTIAL CHANGE REFUSES THE ADMINISTRATOR ARM. A change presents the current credential and is
/// therefore something only its owner can perform; an administrator who must intervene uses the separate
/// reset operation, which is gated on portal administration and recorded as its own administrative act.
/// Admitting an administrator here would collapse the two into one endpoint whose effect depended on which
/// fields were populated, which is precisely the shape that allowed a credential to be overwritten without
/// anyone proving they were entitled to.
/// </para>
/// </remarks>
internal sealed class AccountOwnerRequirement : IAuthorizationRequirement
{
    /// <summary>
    /// Initialises a new instance of the <see cref="AccountOwnerRequirement"/> class.
    /// </summary>
    /// <param name="allowPortalAdministrator">
    /// Whether an administrator of the portal the route names also satisfies the requirement.
    /// </param>
    private AccountOwnerRequirement(bool allowPortalAdministrator) =>
        AllowPortalAdministrator = allowPortalAdministrator;

    /// <summary>
    /// The instance admitting the account holder only.
    /// </summary>
    public static AccountOwnerRequirement OwnerOnly { get; } = new(allowPortalAdministrator: false);

    /// <summary>
    /// The instance admitting the account holder or an administrator of the account's portal.
    /// </summary>
    public static AccountOwnerRequirement OwnerOrPortalAdministrator { get; } =
        new(allowPortalAdministrator: true);

    /// <summary>
    /// Gets a value indicating whether an administrator of the route's portal satisfies the requirement.
    /// </summary>
    public bool AllowPortalAdministrator { get; }
}
