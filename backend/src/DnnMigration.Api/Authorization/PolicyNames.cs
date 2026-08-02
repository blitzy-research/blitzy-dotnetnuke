namespace DnnMigration.Api.Authorization;

/// <summary>
/// The closed catalogue of authorisation policy names for the whole API, so that no controller and
/// no policy registration ever spells a policy as a bare string literal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Declaration and registration are one contract.</b> Every member here must be registered, by
/// this constant, where authorisation is configured, and consumed as
/// <c>[Authorize(Policy = PolicyNames.X)]</c>. A member nothing registers is unusable, and a
/// registration keyed by a literal defeats the catalogue, so adding a member obliges the
/// registration site to gain a matching policy in the same change. Nothing here may be added
/// speculatively.
/// </para>
/// <para>
/// <b>The catalogue is closed, and that is what makes it safe.</b> There is no
/// <c>IAuthorizationPolicyProvider</c> implementation, so a policy name is never parsed,
/// parameterised or synthesised while a request is in flight. A name outside this catalogue
/// therefore cannot be satisfied at all: the framework raises <c>InvalidOperationException</c> as
/// the request is authorised, which is a runtime failure. Referring to policies by symbol moves
/// that failure to compile time, which is the entire purpose of this file.
/// </para>
/// <para>
/// <b>Members must stay <c>const</c>.</b> Each is consumed in an attribute argument position,
/// which accepts compile-time constants only; a field assigned during start-up would not compile
/// there. Each value is identical to its identifier, so a policy is greppable by one token and a
/// mismatch between registration and consumption cannot hide behind a differing string.
/// </para>
/// <para>
/// <b>Why four permission policies and one role policy.</b> Only the view and edit keys are ever
/// evaluated, and only the module and tab scopes can be decided, which bounds the permission group
/// to two keys across two scopes. The read and write keys are the folder keys, and folder
/// permissions are not carried across. The remaining member is the dominant legacy gate, which is
/// not a permission key at all but portal administrator role membership.
/// </para>
/// <para>
/// Each permission member must be registered against a <see cref="PermissionRequirement"/> built
/// from the key and scope named in that member's own documentation, and every decision left to the
/// authorisation handler.
/// </para>
/// </remarks>
public static class PolicyNames
{

    /// <summary>
    /// Grants an action when the caller holds <c>PermissionKey.VIEW</c> on the module identified by
    /// the request, at <c>PermissionScope.Module</c>.
    /// </summary>
    /// <remarks>
    /// An action gated by this policy must carry a module identifier in its route template. One
    /// that does not fails closed, because the handler cannot establish the scope it has to
    /// evaluate and so never succeeds.
    /// </remarks>
    public const string ModuleView = "ModuleView";

    /// <summary>
    /// Grants an action when the caller holds <c>PermissionKey.EDIT</c> on the module identified by
    /// the request, at <c>PermissionScope.Module</c>.
    /// </summary>
    /// <remarks>
    /// An action whose route carries no module identifier fails closed. The single edit key also
    /// covers create, update and delete, which is why the catalogue carries no finer-grained
    /// mutation policy: introducing one would invent a permission the legacy data never stored and
    /// that nothing evaluates.
    /// </remarks>
    public const string ModuleEdit = "ModuleEdit";

    /// <summary>
    /// Grants an action when the caller holds <c>PermissionKey.VIEW</c> on the tab - the legacy
    /// page abstraction - identified by the request, at <c>PermissionScope.Tab</c>.
    /// </summary>
    /// <remarks>
    /// An action whose route carries no tab identifier fails closed. Tab permissions are
    /// inseparable from module placement and portal navigation, which is why the tab scope is
    /// preserved alongside the module scope even though tab administration itself is deliberately
    /// narrow.
    /// </remarks>
    public const string TabView = "TabView";

    /// <summary>
    /// Grants an action when the caller holds <c>PermissionKey.EDIT</c> on the tab - the legacy
    /// page abstraction - identified by the request, at <c>PermissionScope.Tab</c>.
    /// </summary>
    /// <remarks>
    /// An action whose route carries no tab identifier fails closed. As with the module scope, the
    /// edit key covers create, update and delete.
    /// </remarks>
    public const string TabEdit = "TabEdit";

    /// <summary>
    /// Grants an action when the caller is a member of the portal administrator role.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Registered differently, and the difference matters.</b> This is role membership, not a
    /// permission key, so it must be bound to the framework-native role requirement and never to a
    /// <see cref="PermissionRequirement"/>. No key-and-scope pair belongs with it, and no
    /// requirement may be built for this name. It exists because portal administrator membership,
    /// rather than any permission key, is the dominant gate in the legacy administration screens;
    /// without a named constant the controllers would hard-code a role literal, which is what this
    /// catalogue exists to stop.
    /// </para>
    /// <para>
    /// <b>The administrator role name is deliberately not declared here.</b> It is tenant-specific
    /// configuration carried by the portal options, and the registration site must read it from
    /// there. Declaring it here would duplicate an existing contract and put a tenant-specific
    /// value into a tenant-agnostic catalogue.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy gate tested membership imperatively and redirected to an access-denied
    /// view; the declarative equivalent is a 403 from the authorisation middleware, and the
    /// redirect is not reproduced. The legacy condition was also defective - joining the super-user
    /// test to the role test with <c>OrElse</c> redirected a super user away from the screen rather
    /// than past it, the inverse of the evident intent - and that behaviour is not carried across.
    /// This policy gates on portal administrator role membership alone; a super-user flag must
    /// never serve as an authorisation short cut.
    /// </para>
    /// </remarks>
    public const string PortalAdministrator = "PortalAdministrator";
}
