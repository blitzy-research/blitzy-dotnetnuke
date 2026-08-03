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
/// <b>Why four permission policies and four membership policies.</b> Only the view and edit keys are ever
/// evaluated, and only the module and tab scopes can be decided, which bounds the permission group
/// to two keys across two scopes. The read and write keys are the folder keys, and folder
/// permissions are not carried across. The remaining four are not permission keys at all but membership
/// questions, and they are four rather than one because the resources they gate ask four genuinely
/// different questions: does the caller administer the portal in the route, is the caller a host account
/// where no portal is in the route at all, is the caller the account in the route, and is the caller either
/// that account or its portal's administrator. Collapsing any pair of them is what produced the cross-tenant
/// and account-takeover paths this catalogue now closes.
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
    /// <b>The portal decided is the one the ROUTE names.</b> A tenant-scoped route states which portal the
    /// request is about, and that is the portal whose administrator role membership is verified - falling
    /// back to the tenant the caller arrived through only where the route names no portal at all. Deciding
    /// against the arrival tenant instead let an administrator of one portal act on any other, which is the
    /// defect this wording now records.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy gate tested membership imperatively and redirected to an access-denied
    /// view; the declarative equivalent is a 403 from the authorisation middleware, and the
    /// redirect is not reproduced. The legacy condition was also defective - joining the super-user
    /// test to the role test with <c>OrElse</c> redirected a super user away from the screen rather
    /// than past it, the inverse of the evident intent - and that INVERSION is not carried across. The
    /// super-user arm itself is: a host account satisfies this policy, because the permission service
    /// answers every permission question affirmatively for one and the portal service gates the hosting
    /// charge and the quotas on being one, so a portal-administrator gate that refused a host account would
    /// contradict the layer beneath it and would leave a host unable to administer a portal it had just
    /// created. The flag is read from the store rather than from the token, so it is not a claim short cut.
    /// </para>
    /// </remarks>
    public const string PortalAdministrator = "PortalAdministrator";

    /// <summary>
    /// Grants an action when the caller is a host account: the gate for operations that address no single
    /// portal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only for operations with no portal binding of any kind.</b> The portal collection, portal creation,
    /// and the alias resources addressed by their own global identifier. An action that names a portal in its
    /// route must use <see cref="PortalAdministrator"/> instead, which admits a host account anyway; using
    /// this policy there would needlessly refuse the tenant's own administrator.
    /// </para>
    /// <para>
    /// <b>Why it is needed.</b> <see cref="PortalAdministrator"/> falls back to the arrival tenant when the
    /// route names no portal, so for a global operation it asked a truthful but irrelevant question and an
    /// administrator of one tenant could enumerate every tenant, create new ones, or reach another tenant's
    /// alias by guessing its identifier.
    /// </para>
    /// <para>
    /// MIGRATION: this is not the excluded host-administration feature, which is the super-user CONSOLE and
    /// its screens. <c>Users.IsSuperUser</c> is an existing mapped column already emitted as a claim and
    /// already consulted by the permission and portal services; this policy consumes it and adds no screen.
    /// </para>
    /// </remarks>
    public const string HostAdministrator = "HostAdministrator";

    /// <summary>
    /// Grants an action when the caller is the account the route names, and nobody else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the credential change alone. A change presents the current credential, so only its owner can
    /// perform one; an administrator who must intervene uses the reset operation, which carries
    /// <see cref="PortalAdministrator"/> and is recorded as its own administrative act. Admitting an
    /// administrator here would collapse two operations into one whose effect depended on which fields were
    /// populated - the shape that previously allowed a credential to be overwritten with no proof of
    /// entitlement at all.
    /// </para>
    /// <para>
    /// An action gated by this policy must carry an account identifier in its route template. One that does
    /// not fails closed, because ownership cannot be proved without something to compare the subject claim
    /// against.
    /// </para>
    /// </remarks>
    public const string AccountOwner = "AccountOwner";

    /// <summary>
    /// Grants an action when the caller is the account the route names, or an administrator of the portal the
    /// route names.
    /// </summary>
    /// <remarks>
    /// For the account resources that both a holder and an administrator legitimately reach: the account's
    /// own representation and its profile. Requires both an account identifier and a portal identifier in the
    /// route template - the first to prove ownership, the second to scope the administrator arm - and fails
    /// closed without them.
    /// </remarks>
    public const string AccountOwnerOrPortalAdministrator = "AccountOwnerOrPortalAdministrator";
}
