namespace DnnMigration.Api.Authorization;

/// <summary>
/// The closed catalogue of authorisation policy names for the whole API, so that no controller and
/// no policy registration ever spells a policy as a bare string literal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Declaration and registration are one contract.</b> Every member here must be registered, by
/// this constant, where authorisation is configured, and consumed as
/// <c>[Authorize(Policy = PolicyNames.X)]</c>. A member nothing registers is unusable and a
/// registration keyed by a literal defeats the catalogue, so adding a member obliges the
/// registration site to gain a matching policy in the same change. Nothing here may be added
/// speculatively. Members must stay <c>const</c>, because each is consumed in an attribute argument
/// position, and each value is identical to its identifier so a policy is greppable by one token.
/// </para>
/// <para>
/// <b>The catalogue is closed, and that is what makes it safe.</b> There is no
/// <c>IAuthorizationPolicyProvider</c> implementation, so a policy name is never parsed,
/// parameterised or synthesised while a request is in flight; a name outside this catalogue cannot
/// be satisfied at all and the framework raises <c>InvalidOperationException</c> as the request is
/// authorised. Referring to policies by symbol moves that failure to compile time, which is the
/// entire purpose of this file.
/// </para>
/// <para>
/// <b>Every policy FAILS CLOSED when its route lacks the identifier it decides against.</b> Each
/// member below names the route value it requires; an action whose template omits it can never
/// succeed, because the handler cannot establish the scope - or, for the ownership policies, the
/// subject - it has to evaluate. Each permission member is registered against a
/// <see cref="PermissionRequirement"/> built from the key and scope named in its own documentation,
/// with every decision left to the authorisation handler; the membership members are NOT, and their
/// registration is described on each.
/// </para>
/// <para>
/// <b>ONE MEMBER REQUIRES NO ROUTE IDENTIFIER, AND IT IS THE EXCEPTION THAT PROVES THE RULE.</b>
/// <see cref="PortalContentEditor"/> decides at <c>PermissionScope.Portal</c>, which reads no item
/// identifier from the route, because the address it gates names no page: a module's target page
/// arrives in the request body, so there is nothing in the template to resolve. It still fails
/// closed - it is decided against the tenant the request resolves to and the caller's own grants, and
/// a caller holding the key nowhere in that tenant is refused - but it is decided against the tenant
/// rather than against a route value, so the paragraph above does not describe it. It is deliberately
/// the only such member: an address that CAN name the thing it acts on must be gated on a policy that
/// reads that name, because a capability question admits strictly more callers than a resource
/// question, and an endpoint carrying this one stays responsible for narrowing what it returns.
/// </para>
/// <para>
/// <b>Why five permission policies and four membership policies.</b> Only the view and edit keys are
/// ever evaluated - the read and write keys are the folder keys, and folder permissions are not
/// carried across - and they are decided at three scopes. Four of the five pair a key with a scope
/// that names a resource: view and edit at module scope, view and edit at tab scope. In each of those
/// the single EDIT key covers create, update and delete, so no finer-grained mutation policy exists:
/// introducing one would invent a permission the legacy data never stored and nothing evaluates. The
/// fifth, <see cref="PortalContentEditor"/>, pairs the same EDIT key with the portal scope, and it
/// exists because one address cannot name the resource it acts on: placing a module names its target
/// page in the request body, so no resource-scoped policy could gate it without refusing every
/// caller. It is a capability question rather than a resource one, which is why it is one member and
/// not two - there is no portal-scoped VIEW policy, because no address needs to ask whether a caller
/// may read something somewhere. The remaining four are not permission keys at all
/// but membership questions, and they are four rather than one because the resources they gate ask
/// four genuinely different questions: does the caller administer the portal in the route, is the
/// caller a host account where no portal is in the route at all, is the caller the account in the
/// route, and is the caller either that account or its portal's administrator. Collapsing any pair
/// of them is what produced the cross-tenant and account-takeover paths this catalogue closes.
/// </para>
/// </remarks>
public static class PolicyNames
{
    /// <summary>
    /// Grants an action when the caller holds <c>PermissionKey.VIEW</c> on the module identified by
    /// the request, at <c>PermissionScope.Module</c>. Requires a module identifier in the route.
    /// </summary>
    public const string ModuleView = "ModuleView";

    /// <summary>
    /// Grants an action when the caller holds <c>PermissionKey.EDIT</c> on the module identified by
    /// the request, at <c>PermissionScope.Module</c>. Requires a module identifier in the route, and
    /// covers create, update and delete.
    /// </summary>
    public const string ModuleEdit = "ModuleEdit";

    /// <summary>
    /// Grants an action when the caller holds <c>PermissionKey.VIEW</c> on the tab - the legacy page
    /// abstraction - identified by the request, at <c>PermissionScope.Tab</c>. Requires a tab
    /// identifier in the route.
    /// </summary>
    /// <remarks>
    /// Tab permissions are inseparable from module placement and portal navigation, which is why the
    /// tab scope is preserved alongside the module scope even though tab administration itself is
    /// deliberately narrow.
    /// </remarks>
    public const string TabView = "TabView";

    /// <summary>
    /// Grants an action when the caller holds <c>PermissionKey.EDIT</c> on the tab - the legacy page
    /// abstraction - identified by the request, at <c>PermissionScope.Tab</c>. Requires a tab
    /// identifier in the route, and covers create, update and delete.
    /// </summary>
    public const string TabEdit = "TabEdit";

    /// <summary>
    /// Grants an action when the caller is a member of the portal administrator role.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Registered differently, and the difference matters.</b> This is role membership rather than
    /// a permission key, so it must be bound to the framework-native role requirement and never to a
    /// <see cref="PermissionRequirement"/>; no key-and-scope pair belongs with it. It exists because
    /// portal administrator membership, rather than any permission key, is the dominant gate in the
    /// legacy administration screens, and without a named constant the controllers would hard-code a
    /// role literal. The administrator role NAME is deliberately not declared here: it is
    /// tenant-specific configuration carried by the portal options, which the registration site reads
    /// from there.
    /// </para>
    /// <para>
    /// <b>The portal decided is the one the ROUTE names</b>, falling back to the tenant the caller
    /// arrived through only where the route names no portal at all. Deciding against the arrival
    /// tenant instead let an administrator of one portal act on any other.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy gate tested membership imperatively and redirected to an access-denied
    /// view; the declarative equivalent is a 403 from the authorisation middleware, and the redirect
    /// is not reproduced. The legacy condition was also defective - joining the super-user test to
    /// the role test with <c>OrElse</c> redirected a super user away from the screen rather than past
    /// it, the inverse of the evident intent - and that INVERSION is not carried across. The
    /// super-user arm itself is: a host account satisfies this policy, because the permission service
    /// answers every permission question affirmatively for one and the portal service gates the
    /// hosting charge and the quotas on being one, so a portal-administrator gate that refused a host
    /// account would contradict the layer beneath it and leave a host unable to administer a portal it
    /// had just created. The flag is read from the store rather than from the token, so it is not a
    /// claim short cut.
    /// </para>
    /// </remarks>
    public const string PortalAdministrator = "PortalAdministrator";

    /// <summary>
    /// Grants an action when the caller is a host account: the gate for operations that address no
    /// single portal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only for operations with no portal binding of any kind</b> - the portal collection, portal
    /// creation, and the alias resources addressed by their own global identifier. An action that
    /// names a portal in its route must use <see cref="PortalAdministrator"/> instead, which admits a
    /// host account anyway; using this policy there would needlessly refuse the tenant's own
    /// administrator. It is needed because <see cref="PortalAdministrator"/> falls back to the arrival
    /// tenant when the route names no portal, so for a global operation it asked a truthful but
    /// irrelevant question - letting an administrator of one tenant enumerate every tenant, create new
    /// ones, or reach another tenant's alias by guessing its identifier.
    /// </para>
    /// <para>
    /// MIGRATION: this is not the excluded host-administration feature, which is the super-user
    /// CONSOLE and its screens. <c>Users.IsSuperUser</c> is an existing mapped column already emitted
    /// as a claim and already consulted by the permission and portal services; this policy consumes it
    /// and adds no screen.
    /// </para>
    /// </remarks>
    public const string HostAdministrator = "HostAdministrator";

    /// <summary>
    /// Grants an action when the caller is the account the route names, and nobody else. Requires an
    /// account identifier in the route.
    /// </summary>
    /// <remarks>
    /// For the credential change alone. A change presents the current credential, so only its owner
    /// can perform one; an administrator who must intervene uses the reset operation, which carries
    /// <see cref="PortalAdministrator"/> and is recorded as its own administrative act. Admitting an
    /// administrator here would collapse two operations into one whose effect depended on which fields
    /// were populated - the shape that previously allowed a credential to be overwritten with no proof
    /// of entitlement at all.
    /// </remarks>
    public const string AccountOwner = "AccountOwner";

    /// <summary>
    /// Grants an action when the caller is the account the route names, or an administrator of the
    /// portal the route names. Requires both identifiers in the route.
    /// </summary>
    /// <remarks>
    /// For the account resources that both a holder and an administrator legitimately reach: the
    /// account's own representation and its profile. The account identifier proves ownership and the
    /// portal identifier scopes the administrator arm, so the policy fails closed without either.
    /// </remarks>
    public const string AccountOwnerOrPortalAdministrator = "AccountOwnerOrPortalAdministrator";

    /// <summary>
    /// Grants an action when the caller administers the resolved tenant, or holds <c>EDIT</c> on at
    /// least one of its pages. Requires no item identifier in the route.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FOR THE SUPPORTING READS OF AN OPERATION WHOSE TARGET DOES NOT EXIST YET, which is a category
    /// none of the policies above can express. <see cref="ModuleEdit"/> and <see cref="TabEdit"/>
    /// resolve their scope from an item identifier in the route; module CREATION names no page at all,
    /// because the target page arrives in the request body. A form that cannot be filled without two
    /// reads has to admit whoever the create action admits, or the capability is unreachable however
    /// permissive the create action itself is.
    /// </para>
    /// <para>
    /// MIGRATION: this is the gate at <c>ModuleSettings.ascx.vb:L191</c> -
    /// <c>IsInRoles(PortalSettings.AdministratorRoleName)</c> OR
    /// <c>IsInRoles(PortalSettings.ActiveTab.AdministratorRoles)</c>, the tenant's administrators or the
    /// roles holding EDIT on the page being administered from. The legacy active page was ambient
    /// request state this solution does not have, so the second arm generalises to any page the caller
    /// holds EDIT on: the narrowest faithful reading, admitting exactly the callers who could have
    /// arrived from some page.
    /// </para>
    /// <para>
    /// ⚠ IT IS NARROWER THAN <see cref="PortalAdministrator"/> FOR NOBODY AND WIDER FOR ONE CALLER, so
    /// an endpoint moved onto it must narrow what it RETURNS. Admission says the caller holds EDIT
    /// somewhere in the tenant; it does not say which pages. A page listing offered under this policy
    /// therefore has to be filtered to the pages the caller may act on, or the policy would turn a
    /// page-administrator's capability into a licence to enumerate the tenant's whole navigation
    /// hierarchy - which is precisely the disclosure the tenant-administration policy was applied to
    /// those routes to close. A caller holding EDIT nowhere is refused outright, so nothing is widened
    /// for the callers that policy was protecting against.
    /// </para>
    /// </remarks>
    public const string PortalContentEditor = "PortalContentEditor";
}
