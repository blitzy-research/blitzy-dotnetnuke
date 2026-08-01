namespace DnnMigration.Api.Authorization;

/// <summary>
/// The closed catalogue of ASP.NET Core authorisation policy names for the whole
/// API, so that no controller and no policy-registration call site ever spells a
/// policy as a bare string literal.
/// </summary>
/// <remarks>
/// <para>
/// Every member below is registered explicitly, by this constant, in
/// <c>Extensions/AuthenticationExtensions.cs</c>, and consumed by the controllers
/// under <c>Controllers/</c> as <c>[Authorize(Policy = PolicyNames.X)]</c>. The two
/// halves are one contract: a member declared here that nothing registers is
/// unusable, and a registration keyed by a literal rather than by one of these
/// members defeats the point of the catalogue. Adding a member therefore obliges
/// the registration site to gain a matching policy in the same change, so nothing
/// here may be added speculatively.
/// </para>
/// <para>
/// The catalogue is deliberately CLOSED. This solution contains no
/// <c>IAuthorizationPolicyProvider</c> implementation, so a policy name is never
/// parsed, parameterised or synthesised while a request is in flight - there is
/// nothing resembling <c>Permission:EDIT:Module:42</c> anywhere in the design. A
/// name that is not a member of this catalogue consequently cannot be satisfied at
/// all: the framework raises <c>InvalidOperationException</c> as the request is
/// authorised, which is a runtime failure rather than a compile-time one. Removing
/// that failure mode - by making every policy reference a symbol the compiler
/// checks - is the entire purpose of this file.
/// </para>
/// <para>
/// Members are <c>const</c>, and not fields assigned during start-up, because each
/// is consumed in an attribute argument position, which accepts compile-time
/// constants only; a non-constant field would not compile there. Each value is
/// identical to its identifier, so every policy is greppable by a single token and
/// a mismatch between registration and consumption cannot hide behind a differing
/// string.
/// </para>
/// <para>
/// The vocabulary was measured rather than invented. Across the entire legacy tree
/// exactly two permission keys reach an evaluation call site - PermissionKey.EDIT
/// at seven of the eight sites, PermissionKey.VIEW at the remaining one - and the
/// target permission evaluator exposes decisions for the Module and Tab scopes
/// only. Two keys across two scopes is what bounds Group A to four members. The
/// READ and WRITE keys are absent deliberately: they are the folder keys, and the
/// only calls that pass them - in
/// <c>Library/Components/FileSystem/FileSystemUtils.vb</c> at L709 and L1580 - go
/// through the folder permission path, which this migration does not carry across.
/// Group B holds the dominant legacy gate, which is not a permission key at all but
/// portal administrator role membership.
/// </para>
/// </remarks>
public static class PolicyNames
{
    // -------------------------------------------------------------------------
    // GROUP A - PERMISSION POLICIES.
    //
    // Each member is registered against a PermissionRequirement built from the key
    // and scope pair named in its own documentation, and every decision is taken by
    // PermissionAuthorizationHandler.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Grants an action when the caller holds PermissionKey.VIEW on the module
    /// identified by the request, at PermissionScope.Module.
    /// </summary>
    /// <remarks>
    /// Registered against a <c>PermissionRequirement</c> and decided by
    /// <c>PermissionAuthorizationHandler</c>, which resolves the module from route
    /// data. An action gated by this policy must therefore carry a module
    /// identifier in its route template; one that does not will fail closed,
    /// because the handler cannot establish the scope it has to evaluate and so
    /// never succeeds. Replaces the single legacy evaluation of the view key: the
    /// call to <c>ModulePermissionController.HasModulePermission</c> against a
    /// module permission collection at
    /// <c>Website/admin/Packages/Install.ascx.vb:L215</c>.
    /// </remarks>
    public const string ModuleView = "ModuleView";

    /// <summary>
    /// Grants an action when the caller holds PermissionKey.EDIT on the module
    /// identified by the request, at PermissionScope.Module.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registered against a <c>PermissionRequirement</c> and decided by
    /// <c>PermissionAuthorizationHandler</c>, which resolves the module from route
    /// data; an action whose route carries no module identifier fails closed.
    /// </para>
    /// <para>
    /// This is the busiest key in the legacy tree. The edit key is evaluated at
    /// four sites in <c>Library/Components/Security/PortalSecurity.vb</c> - L522,
    /// then L618, L623 and L628, the last three being the trio of
    /// deprecation-marked <c>HasEditPermissions</c> wrappers spanning L616 to L629,
    /// each of which hard-codes the key - and again at
    /// <c>Website/admin/Packages/Install.ascx.vb:L212</c>.
    /// </para>
    /// <para>
    /// The single edit key also covers create, update and delete, which is why the
    /// catalogue carries no finer-grained mutation policy: introducing one would
    /// invent a permission the legacy data never stored and that nothing evaluates.
    /// </para>
    /// </remarks>
    public const string ModuleEdit = "ModuleEdit";

    /// <summary>
    /// Grants an action when the caller holds PermissionKey.VIEW on the tab - the
    /// legacy page abstraction - identified by the request, at
    /// PermissionScope.Tab.
    /// </summary>
    /// <remarks>
    /// Registered against a <c>PermissionRequirement</c> and decided by
    /// <c>PermissionAuthorizationHandler</c>, which resolves the tab from route
    /// data; an action whose route carries no tab identifier fails closed. Tab
    /// permissions are inseparable from module placement and portal navigation,
    /// which is why the tab scope is preserved alongside the module scope even
    /// though tab administration itself is deliberately narrow. The legacy
    /// evaluation entry points are
    /// <c>TabPermissionController.HasTabPermission</c> at
    /// <c>Library/Components/Security/Permissions/TabPermissionController.vb</c>
    /// L33 and L38.
    /// </remarks>
    public const string TabView = "TabView";

    /// <summary>
    /// Grants an action when the caller holds PermissionKey.EDIT on the tab - the
    /// legacy page abstraction - identified by the request, at
    /// PermissionScope.Tab.
    /// </summary>
    /// <remarks>
    /// Registered against a <c>PermissionRequirement</c> and decided by
    /// <c>PermissionAuthorizationHandler</c>, which resolves the tab from route
    /// data; an action whose route carries no tab identifier fails closed. Replaces
    /// the two legacy tab-scoped edit checks, each a call to
    /// <c>TabPermissionController.HasTabPermission</c> carrying the edit key, at
    /// <c>Website/admin/Authentication/Login.ascx.vb:L646</c> and
    /// <c>Website/Default.aspx.vb:L560</c>. As with the module scope, the edit key
    /// covers create, update and delete.
    /// </remarks>
    public const string TabEdit = "TabEdit";

    // -------------------------------------------------------------------------
    // GROUP B - ROLE POLICY. REGISTERED DIFFERENTLY, AND THE DIFFERENCE MATTERS.
    //
    // The member below is bound in Extensions/AuthenticationExtensions.cs to the
    // framework-native role requirement - RequireRole - and NOT to a
    // PermissionRequirement. PermissionAuthorizationHandler therefore never sees
    // it, and no key-and-scope pair belongs with it. Do not attempt to build a
    // PermissionRequirement for this name.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Grants an action when the caller is a member of the portal administrator
    /// role. This is role membership, not a permission key, so it is registered
    /// with the framework-native role requirement and is never seen by
    /// <c>PermissionAuthorizationHandler</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This member exists because portal administrator role membership, and not any
    /// permission key, is the dominant gate in the legacy administration screens.
    /// The canonical site is <c>Website/admin/Security/SecurityRoles.ascx.vb</c>
    /// L318 to L324, whose <c>DataBind</c> override tests
    /// <c>PortalSecurity.IsInRoles(PortalSettings.AdministratorRoleName)</c> at L322
    /// and redirects at L323. Nine further occurrences of the same test were
    /// measured across the screens in scope:
    /// <c>Website/admin/Modules/ModuleSettings.ascx.vb</c> L191, L215 and L333, and
    /// <c>Website/admin/Tabs/ManageTabs.ascx.vb</c> L170, L476, L555, L586, L601
    /// and L622. Without a named constant for this gate the controllers would
    /// hard-code a role literal, which is exactly what this catalogue exists to
    /// stop.
    /// </para>
    /// <para>
    /// The administrator role name itself is NOT declared here. It is configuration
    /// belonging to the portal options bound by the Application layer, and the
    /// registration site reads it from there. Declaring it here would duplicate a
    /// contract that already exists, and would put a tenant-specific value into a
    /// tenant-agnostic catalogue.
    /// </para>
    /// <para>
    /// The legacy mechanism was imperative: test, then redirect to the access-denied
    /// view, which rendered its message at
    /// <c>Website/admin/Security/AccessDenied.ascx.vb:L45</c>. The declarative
    /// equivalent is a 403 produced by the authorisation middleware; the redirect is
    /// not reproduced.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy condition at the canonical site is itself defective.
    /// Because the super-user test is joined to the role test with <c>OrElse</c>, a
    /// super user short-circuits the condition to true and is redirected to the
    /// access-denied view rather than past it - the inverse of the evident intent.
    /// The defect is annotated rather than fixed, and it is not reproduced here:
    /// this policy gates on portal administrator role membership alone, and nothing
    /// about the defect is encoded in the policy name. Super-user administration is
    /// beyond the scope of this migration, and a super-user flag must never serve as
    /// an authorisation short cut.
    /// </para>
    /// </remarks>
    public const string PortalAdministrator = "PortalAdministrator";
}
