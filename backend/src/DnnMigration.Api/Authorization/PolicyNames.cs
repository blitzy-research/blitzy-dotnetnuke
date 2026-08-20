namespace DnnMigration.Api.Authorization;

/// <summary>
/// The closed catalogue of authorisation policy names for the whole API, so that no controller and no
/// policy registration ever spells a policy as a bare string literal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Declaration and registration are one contract.</b> Every member here must be registered, by this
/// constant, where authorisation is configured, and consumed as <c>[Authorize(Policy = PolicyNames.X)]</c>.
/// A member nothing registers is unusable and a registration keyed by a literal defeats the catalogue, so
/// adding a member obliges the registration site to gain a matching policy in the same change.
/// </para>
/// <para>
/// <b>The catalogue is closed, and that is what makes it safe.</b> There is no
/// <c>IAuthorizationPolicyProvider</c> implementation, so a policy name is never parsed, parameterised or
/// synthesised while a request is in flight; a name outside this catalogue cannot be satisfied at all and
/// the framework raises <c>InvalidOperationException</c> as the request is authorised.
/// </para>
/// </remarks>
public static class PolicyNames
{
    /// <summary>
    /// Grants an action when the caller holds <c>PermissionKey.VIEW</c> on the module identified by the
    /// request, at <c>PermissionScope.Module</c>. Requires a module identifier in the route.
    /// </summary>
    public const string ModuleView = "ModuleView";

    /// <summary>
    /// Grants an action when the caller holds <c>PermissionKey.EDIT</c> on the module identified by the
    /// request, at <c>PermissionScope.Module</c>. Requires a module identifier in the route, and covers
    /// create, update and delete.
    /// </summary>
    public const string ModuleEdit = "ModuleEdit";

    /// <summary>
    /// Grants an action when the caller holds <c>PermissionKey.VIEW</c> on the tab - the legacy page
    /// abstraction - identified by the request, at <c>PermissionScope.Tab</c>. Requires a tab identifier in
    /// the route.
    /// </summary>
    public const string TabView = "TabView";

    /// <summary>
    /// Grants an action when the caller holds <c>PermissionKey.EDIT</c> on the tab - the legacy page
    /// abstraction - identified by the request, at <c>PermissionScope.Tab</c>. Requires a tab identifier in
    /// the route, and covers create, update and delete.
    /// </summary>
    public const string TabEdit = "TabEdit";

    /// <summary>Grants an action when the caller is a member of the portal administrator role.</summary>
    /// <remarks>
    /// MIGRATION: the legacy gate tested membership imperatively and redirected to an access-denied view;
    /// the declarative equivalent is a 403 from the authorisation middleware, and the redirect is not
    /// reproduced.
    /// </remarks>
    public const string PortalAdministrator = "PortalAdministrator";

    /// <summary>
    /// Grants an action when the caller is a host account: the gate for operations that address no single
    /// portal.
    /// </summary>
    /// <remarks>
    /// <b>Only for operations with no portal binding of any kind</b> - the portal collection, portal
    /// creation, and the alias resources addressed by their own global identifier. An action that names a
    /// portal in its route must use <see cref="PortalAdministrator"/> instead, which admits a host account
    /// anyway; using this policy there would needlessly refuse the tenant's own administrator.
    /// </remarks>
    public const string HostAdministrator = "HostAdministrator";

    /// <summary>
    /// Grants an action when the caller is the account the route names, and nobody else. Requires an
    /// account identifier in the route.
    /// </summary>
    public const string AccountOwner = "AccountOwner";

    /// <summary>
    /// Grants an action when the caller is the account the route names, or an administrator of the portal
    /// the route names. Requires both identifiers in the route.
    /// </summary>
    /// <remarks>
    /// For the account resources that both a holder and an administrator legitimately reach: the account's
    /// own representation and its profile. The account identifier proves ownership and the portal
    /// identifier scopes the administrator arm, so the policy fails closed without either.
    /// </remarks>
    public const string AccountOwnerOrPortalAdministrator = "AccountOwnerOrPortalAdministrator";

    /// <summary>
    /// Grants an action when the caller administers the resolved tenant, or holds <c>EDIT</c> on at least
    /// one of its pages. Requires no item identifier in the route.
    /// </summary>
    /// <remarks>
    /// FOR THE SUPPORTING READS OF AN OPERATION WHOSE TARGET DOES NOT EXIST YET, which is a category none
    /// of the policies above can express. <see cref="ModuleEdit"/> and <see cref="TabEdit"/> resolve their
    /// scope from an item identifier in the route; module CREATION names no page at all, because the target
    /// page arrives in the request body.
    /// </remarks>
    public const string PortalContentEditor = "PortalContentEditor";
}
