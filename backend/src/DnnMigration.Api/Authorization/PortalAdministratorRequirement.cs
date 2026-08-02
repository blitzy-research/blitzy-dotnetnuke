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
