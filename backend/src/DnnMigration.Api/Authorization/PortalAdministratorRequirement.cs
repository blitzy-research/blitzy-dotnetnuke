using Microsoft.AspNetCore.Authorization;

namespace DnnMigration.Api.Authorization;

/// <summary>Requires that the caller administers the portal that the current request resolved to.</summary>
/// <remarks>
/// <para>
/// WHY THIS TYPE EXISTS AT ALL, WHEN THE FRAMEWORK ALREADY SHIPS A ROLE REQUIREMENT. Because the
/// framework's role requirement compares a role NAME against the caller's claims and has no concept of a
/// tenant, and in this schema administrator role identity is per-tenant. Three facts about the legacy
/// database combine into the defect:
/// </para>
/// <para>
/// First, each portal names its own administrator role: <c>Portals.AdministratorRoleId</c> is a real
/// nullable column (<c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> line
/// 91), so the identity of "the administrator role" is a per-row fact rather than a per-installation one.
/// </para>
/// </remarks>
internal sealed class PortalAdministratorRequirement : IAuthorizationRequirement
{
    /// <summary>The single shared instance.</summary>
    /// <remarks>
    /// Safe to share precisely because the type carries no state; offered as a member so that a
    /// registration site cannot be tempted to construct a per-tenant variant.
    /// </remarks>
    public static PortalAdministratorRequirement Instance { get; } = new();
}

/// <summary>Requires that the caller is a host account, for the operations that address no single portal.</summary>
/// <remarks>
/// <para>
/// WHY THE PORTAL REQUIREMENT CANNOT SERVE THESE OPERATIONS. A handful of endpoints carry no portal binding
/// at all - the portal collection, portal creation, and the alias resources addressed by their own global
/// identifier.
/// </para>
/// <para>
/// WHY THIS IS NOT THE EXCLUDED HOST-ADMINISTRATION FEATURE. What the migration excludes is the super-user
/// CONSOLE and the screens reachable only from it.
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
/// WHY THE ADMINISTRATOR ARM IS A FLAG RATHER THAN A SECOND TYPE. The two policies differ in exactly one
/// bit, and the alternative - two requirement types with two handlers - would duplicate the ownership test
/// so that the two could drift apart on the one comparison that matters. The flag is set at registration,
/// never per request, so the instances stay shareable.
/// </para>
/// <para>
/// WHY THE CREDENTIAL CHANGE REFUSES THE ADMINISTRATOR ARM. A change presents the current credential and is
/// therefore something only its owner can perform; an administrator who must intervene uses the separate
/// reset operation, which is gated on portal administration and recorded as its own administrative act.
/// </para>
/// </remarks>
internal sealed class AccountOwnerRequirement : IAuthorizationRequirement
{
    /// <summary>Initialises a new instance of the <see cref="AccountOwnerRequirement"/> class.</summary>
    /// <param name="allowPortalAdministrator">
    /// Whether an administrator of the portal the route names also satisfies the requirement.
    /// </param>
    private AccountOwnerRequirement(bool allowPortalAdministrator) =>
        AllowPortalAdministrator = allowPortalAdministrator;

    /// <summary>The instance admitting the account holder only.</summary>
    public static AccountOwnerRequirement OwnerOnly { get; } = new(allowPortalAdministrator: false);

    /// <summary>The instance admitting the account holder or an administrator of the account's portal.</summary>
    public static AccountOwnerRequirement OwnerOrPortalAdministrator { get; } =
        new(allowPortalAdministrator: true);

    /// <summary>
    /// Gets a value indicating whether an administrator of the route's portal satisfies the requirement.
    /// </summary>
    public bool AllowPortalAdministrator { get; }
}
