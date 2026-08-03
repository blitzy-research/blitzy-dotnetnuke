// MIGRATION: this service replaces the three legacy permission controllers -
// Library/Components/Security/Permissions/PermissionController.vb (9 public members),
// ModulePermissionController.vb (18) and TabPermissionController.vb (15) - which between them
// duplicated the same permission-string evaluation three times over.
//
// MIGRATION: allow-and-deny precedence is NOT re-derived here. The permission repository owns the
// single evaluator, and its contract states the rule it applies: a denying entry suppresses the key
// even when another entry allows it. Re-implementing that ordering in this layer would create a second
// evaluator that could disagree with the first, which is precisely the duplication the three legacy
// controllers suffered from.
//
// MIGRATION: what this service does own is the caller-to-role-names rule, because that rule is business
// logic rather than a query. It reproduces PortalSecurity.IsInRoles (PortalSecurity.vb:L114-L134)
// exactly: a host account holds every permission unconditionally; the "All Users" pseudo-role applies to
// every caller, authenticated or not; and the "Unauthenticated Users" pseudo-role applies only while the
// caller is anonymous. The legacy member read the caller from ambient request state and from a cookie it
// wrote at PortalSecurity.vb:L98; here the caller is always named by argument.
//
// MIGRATION: the legacy grant-management members - adding, updating and deleting individual
// access-control entries - are deliberately not exposed on this contract. Grant editing belonged to the
// permission-grid control that the excluded control library supplied, and no admin screen in scope
// reaches it. The repository still declares the staging members, so the capability is present in the
// layer that owns it whenever a grant-editing surface is added.
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Options;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Services;

/// <summary>
/// Answers permission questions within a portal: what the catalogue defines, what a caller effectively
/// holds, whether a caller holds one named key on one module or page, and the cleanup a deleted account
/// requires.
/// </summary>
/// <remarks>
/// <para>
/// Every member is tenant-scoped and every caller is named by argument, so no answer here depends on
/// ambient request state.
/// </para>
/// <para>
/// This service resolves the caller into a set of role names and then delegates the evaluation itself to
/// the permission repository, which owns the single allow-and-deny evaluator. It therefore contains the
/// rules about <em>who the caller is</em> and none of the rules about <em>how grants combine</em>.
/// </para>
/// </remarks>
public sealed class PermissionService : IPermissionService
{
    /// <summary>
    /// Reported when a catalogue filter is supplied in a form that cannot match anything.
    /// </summary>
    private const string FilterInvalidCode = "permission.filter_invalid";

    /// <summary>
    /// Reported when the named portal does not exist.
    /// </summary>
    private const string PortalNotFoundCode = "permission.portal_not_found";

    /// <summary>
    /// Reported when the named module does not exist within the portal.
    /// </summary>
    private const string ModuleNotFoundCode = "permission.module_not_found";

    /// <summary>
    /// Reported when the named page does not exist within the portal.
    /// </summary>
    private const string TabNotFoundCode = "permission.tab_not_found";

    /// <summary>
    /// Reported when the named caller does not exist.
    /// </summary>
    private const string UserNotFoundCode = "permission.user_not_found";

    /// <summary>
    /// Reported when the submitted key is not a defined member of the closed key set.
    /// </summary>
    private const string KeyInvalidCode = "permission.key_invalid";

    /// <summary>
    /// Lowest value <c>dbo.ModuleDefinitions.ModuleDefID</c> can take, the column being
    /// <c>IDENTITY (1, 1)</c>, so a smaller value cannot name a row.
    /// </summary>
    private const int LowestModuleDefinitionId = 1;

    /// <summary>
    /// The view key, named once because the inherit-view rule is the only place a key is special-cased.
    /// </summary>
    private const string ViewPermissionKey = "VIEW";

    /// <summary>
    /// Page size that requests every match unpaged, per the repository contracts.
    /// </summary>
    private const int UnpagedPageSize = 0;

    private readonly IPermissionRepository _permissions;
    private readonly IPortalRepository _portals;
    private readonly IModuleRepository _modules;
    private readonly ITabRepository _tabs;
    private readonly IUserRepository _users;
    private readonly IClock _clock;
    private readonly PortalOptions _portalOptions;

    /// <summary>
    /// Initialises the service with the collaborators it resolves callers and scopes through.
    /// </summary>
    /// <param name="permissions">The permission catalogue, the grants, and the single evaluator.</param>
    /// <param name="portals">Portal existence.</param>
    /// <param name="modules">Module existence and, for the inherit-view rule, module placement.</param>
    /// <param name="tabs">Page existence.</param>
    /// <param name="users">Caller resolution and the caller's role names.</param>
    /// <param name="clock">The instant role assignments are evaluated as of.</param>
    /// <param name="portalOptions">Bound configuration supplying the two pseudo-role names.</param>
    public PermissionService(
        IPermissionRepository permissions,
        IPortalRepository portals,
        IModuleRepository modules,
        ITabRepository tabs,
        IUserRepository users,
        IClock clock,
        PortalOptions portalOptions)
    {
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _portals = portals ?? throw new ArgumentNullException(nameof(portals));
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _portalOptions = portalOptions ?? throw new ArgumentNullException(nameof(portalOptions));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The two filters compose conjunctively, so supplying neither returns the whole catalogue. The scope
    /// code is deliberately matched as free text rather than against an enumeration, because the column it
    /// lives in is free text and an installation carrying a code this codebase has never seen must still
    /// round-trip intact.
    /// </remarks>
    public async Task<Result<IReadOnlyList<string>>> GetPermissionKeysAsync(
        string? permissionCode = null,
        int? moduleDefinitionId = null,
        CancellationToken cancellationToken = default)
    {
        if (permissionCode is not null && string.IsNullOrWhiteSpace(permissionCode))
        {
            return Result<IReadOnlyList<string>>.Failure(
                FilterInvalidCode,
                "The permission code filter must not be blank; omit it to place no restriction.");
        }

        if (moduleDefinitionId is int definitionId && definitionId < LowestModuleDefinitionId)
        {
            return Result<IReadOnlyList<string>>.Failure(
                FilterInvalidCode,
                FormattableString.Invariant(
                    $"Module definition {definitionId} cannot name a row; identifiers start at {LowestModuleDefinitionId}."));
        }

        IReadOnlyList<Permission> catalogue =
            await _permissions.ListAsync(permissionCode, moduleDefinitionId, cancellationToken)
                .ConfigureAwait(false);

        // MIGRATION: Permission.PermissionKey is the closed PermissionKey enumeration, whose member
        // NAME is the stored and wire value, so the projection is ToString() rather than a lookup and
        // it can only ever yield VIEW, EDIT, READ or WRITE. Normalise still runs: it de-duplicates the
        // catalogue and imposes the ordinal ordering the contract promises, and it is the same
        // treatment the grant-derived answers below receive, which do arrive as arbitrary column text.
        return Result<IReadOnlyList<string>>.Success(Normalise(
            catalogue.Select(entry => entry.PermissionKey.ToString())));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Supplying neither scope answers at portal level - the union of everything the caller holds anywhere
    /// in the portal - which is the set an administration shell needs to decide which sections to offer and
    /// the set that populates an access token's permission claims. Supplying either scope, or both,
    /// narrows the answer to it.
    /// </para>
    /// <para>
    /// A host account holds every permission, so it is answered with the whole catalogue rather than being
    /// evaluated against grants. That reproduces the legacy role test, whose very first condition returned
    /// true for a host account before any role was examined.
    /// </para>
    /// <para>
    /// A module configured to inherit its view permission is answered for that one key from the pages it
    /// is placed on, exactly as the legacy read did; every other key still comes from the module's own
    /// grants.
    /// </para>
    /// </remarks>
    public async Task<Result<IReadOnlyList<string>>> GetEffectivePermissionKeysAsync(
        int portalId,
        int? userId,
        int? moduleId = null,
        int? tabId = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result<IReadOnlyList<string>>.Failure(
                PortalNotFoundCode,
                FormattableString.Invariant($"Portal {portalId} does not exist."));
        }

        CallerIdentity caller = await ResolveCallerAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (!caller.Found)
        {
            return Result<IReadOnlyList<string>>.Failure(
                UserNotFoundCode,
                FormattableString.Invariant($"Account {userId} does not exist in portal {portalId}."));
        }

        if (caller.IsSuperUser)
        {
            IReadOnlyList<Permission> catalogue = await _permissions
                .ListAsync(permissionCode: null, moduleDefinitionId: null, cancellationToken)
                .ConfigureAwait(false);

            // MIGRATION: see GetPermissionKeysAsync - the enumeration member name is the stored value,
            // so a host account is answered with the catalogue's own key names and nothing is translated.
            return Result<IReadOnlyList<string>>.Success(Normalise(
                catalogue.Select(entry => entry.PermissionKey.ToString())));
        }

        Module? module = null;
        if (moduleId is int scopedModuleId)
        {
            module = await _modules
                .GetAsync(scopedModuleId, includePlacements: true, cancellationToken)
                .ConfigureAwait(false);

            if (module is null || !BelongsToPortal(module.PortalId, portalId))
            {
                return Result<IReadOnlyList<string>>.Failure(
                    ModuleNotFoundCode,
                    FormattableString.Invariant(
                        $"Module {scopedModuleId} does not exist in portal {portalId}."));
            }
        }

        if (tabId is int scopedTabId)
        {
            Tab? tab = await _tabs.GetByIdAsync(scopedTabId, cancellationToken).ConfigureAwait(false);
            if (tab is null || !BelongsToPortal(tab.PortalId, portalId))
            {
                return Result<IReadOnlyList<string>>.Failure(
                    TabNotFoundCode,
                    FormattableString.Invariant($"Page {scopedTabId} does not exist in portal {portalId}."));
            }
        }

        if (module is null && tabId is null)
        {
            IReadOnlyList<string> portalWide = await _permissions
                .ListEffectivePortalPermissionKeysAsync(portalId, userId, caller.RoleNames, cancellationToken)
                .ConfigureAwait(false);

            return Result<IReadOnlyList<string>>.Success(Normalise(portalWide));
        }

        var keys = new List<string>();

        if (module is not null)
        {
            IReadOnlyList<string> moduleKeys = await _permissions
                .ListEffectiveModulePermissionKeysAsync(module.ModuleId, userId, caller.RoleNames, cancellationToken)
                .ConfigureAwait(false);

            if (module.InheritViewPermissions == true)
            {
                // The module takes its view permission from the pages it sits on, so its own view grant is
                // disregarded and the page grants decide, which is what ModuleController.vb:L131-L136 did.
                keys.AddRange(moduleKeys.Where(key =>
                    !string.Equals(key, ViewPermissionKey, StringComparison.OrdinalIgnoreCase)));

                if (await InheritedViewGrantedAsync(module, userId, caller.RoleNames, cancellationToken)
                    .ConfigureAwait(false))
                {
                    keys.Add(ViewPermissionKey);
                }
            }
            else
            {
                keys.AddRange(moduleKeys);
            }
        }

        if (tabId is int pageId)
        {
            IReadOnlyList<string> tabKeys = await _permissions
                .ListEffectiveTabPermissionKeysAsync(pageId, userId, caller.RoleNames, cancellationToken)
                .ConfigureAwait(false);

            keys.AddRange(tabKeys);
        }

        return Result<IReadOnlyList<string>>.Success(Normalise(keys));
    }

    /// <inheritdoc />
    /// <remarks>
    /// A denial is a successful result carrying <see langword="false"/>, never a failure: the caller asked
    /// a question and got an answer. Only an unanswerable question - a module that does not exist, or a key
    /// outside the closed set - is reported as a failure.
    /// </remarks>
    public async Task<Result<bool>> HasModulePermissionAsync(
        int portalId,
        int? userId,
        int moduleId,
        PermissionKey permissionKey,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(permissionKey))
        {
            return Result<bool>.Failure(
                KeyInvalidCode,
                FormattableString.Invariant($"Permission key {(int)permissionKey} is not defined."));
        }

        Module? module = await _modules
            .GetAsync(moduleId, includePlacements: true, cancellationToken)
            .ConfigureAwait(false);

        if (module is null || !BelongsToPortal(module.PortalId, portalId))
        {
            return Result<bool>.Failure(
                ModuleNotFoundCode,
                FormattableString.Invariant($"Module {moduleId} does not exist in portal {portalId}."));
        }

        CallerIdentity caller = await ResolveCallerAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (!caller.Found)
        {
            return Result<bool>.Success(false);
        }

        if (caller.IsSuperUser)
        {
            return Result<bool>.Success(true);
        }

        if (permissionKey == PermissionKey.VIEW && module.InheritViewPermissions == true)
        {
            bool inherited = await InheritedViewGrantedAsync(module, userId, caller.RoleNames, cancellationToken)
                .ConfigureAwait(false);

            return Result<bool>.Success(inherited);
        }

        bool granted = await _permissions
            .HasModulePermissionAsync(moduleId, permissionKey, userId, caller.RoleNames, cancellationToken)
            .ConfigureAwait(false);

        return Result<bool>.Success(granted);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The page is always named by argument. The legacy pair this replaces included one member that read
    /// the page from ambient request state, so the same question produced different answers depending on
    /// which member the caller happened to reach.
    /// </remarks>
    public async Task<Result<bool>> HasTabPermissionAsync(
        int portalId,
        int? userId,
        int tabId,
        PermissionKey permissionKey,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(permissionKey))
        {
            return Result<bool>.Failure(
                KeyInvalidCode,
                FormattableString.Invariant($"Permission key {(int)permissionKey} is not defined."));
        }

        Tab? tab = await _tabs.GetByIdAsync(tabId, cancellationToken).ConfigureAwait(false);
        if (tab is null || !BelongsToPortal(tab.PortalId, portalId))
        {
            return Result<bool>.Failure(
                TabNotFoundCode,
                FormattableString.Invariant($"Page {tabId} does not exist in portal {portalId}."));
        }

        CallerIdentity caller = await ResolveCallerAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (!caller.Found)
        {
            return Result<bool>.Success(false);
        }

        if (caller.IsSuperUser)
        {
            return Result<bool>.Success(true);
        }

        bool granted = await _permissions
            .HasTabPermissionAsync(tabId, permissionKey, userId, caller.RoleNames, cancellationToken)
            .ConfigureAwait(false);

        return Result<bool>.Success(granted);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Only grants made directly to the account are removed. Grants the account receives through a role
    /// belong to the role rather than to the account, so removing them would strip every other holder of
    /// that role as well.
    /// </para>
    /// <para>
    /// The removal spans both grant tables and is committed as one unit by the repository member, whose
    /// contract makes it immediate rather than staged and has it report how many entries it removed.
    /// Removing nothing is a legitimate outcome, so an account that held no direct grants succeeds.
    /// </para>
    /// <para>
    /// The portal is taken as an argument deliberately. The two legacy cleanups read it off the account
    /// object they were handed, so an account-only contract would silently widen the removal to every
    /// portal the account belongs to.
    /// </para>
    /// </remarks>
    public async Task<Result> DeleteUserPermissionsAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure(
                PortalNotFoundCode,
                FormattableString.Invariant($"Portal {portalId} does not exist."));
        }

        CallerIdentity caller = await ResolveCallerAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (!caller.Found)
        {
            return Result.Failure(
                UserNotFoundCode,
                FormattableString.Invariant($"Account {userId} does not exist in portal {portalId}."));
        }

        await _permissions.DeleteUserPermissionsAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success();
    }

    /// <summary>
    /// Decides whether a module configured to inherit its view permission is viewable by the caller.
    /// </summary>
    /// <param name="module">The module, with its placements loaded.</param>
    /// <param name="userId">The caller, or <see langword="null"/> when anonymous.</param>
    /// <param name="roleNames">The role names the caller holds.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns><see langword="true"/> when at least one page the module sits on grants the view key.</returns>
    /// <remarks>
    /// MIGRATION: the legacy read answered this for one module <em>instance on one page</em>, because the
    /// object it hydrated was a flattened module-and-placement join that always carried a page identifier.
    /// This contract names only the module, so the answer is taken across every page the module is placed
    /// on: the module is viewable when at least one of those pages grants the view key. For a module with
    /// a single placement - the overwhelming common case - that is exactly the legacy answer. A caller that
    /// needs the page-scoped question answered asks it directly of the page.
    /// </remarks>
    private async Task<bool> InheritedViewGrantedAsync(
        Module module,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TabModule> placements = module.TabModules.Count > 0
            ? module.TabModules.ToList()
            : await _modules.ListPlacementsAsync(module.ModuleId, cancellationToken).ConfigureAwait(false);

        foreach (TabModule placement in placements)
        {
            bool granted = await _permissions
                .HasTabPermissionAsync(
                    placement.TabId,
                    PermissionKey.VIEW,
                    userId,
                    roleNames,
                    cancellationToken)
                .ConfigureAwait(false);

            if (granted)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves a caller into the role names the evaluator must consider.
    /// </summary>
    /// <param name="portalId">The portal the question is asked within.</param>
    /// <param name="userId">The caller, or <see langword="null"/> when anonymous.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>Whether the caller was found, whether it is a host account, and its role names.</returns>
    /// <remarks>
    /// <para>
    /// This reproduces PortalSecurity.IsInRoles (PortalSecurity.vb:L114-L134) member for member. The
    /// "All Users" pseudo-role is added for every caller, and the "Unauthenticated Users" pseudo-role only
    /// while the caller is anonymous, because the legacy test admitted it only when the request was not
    /// authenticated.
    /// </para>
    /// <para>
    /// A host account is accepted even when it holds no membership of the portal, because a host account is
    /// installation-wide and the legacy test short-circuited on it before examining any role.
    /// </para>
    /// </remarks>
    private async Task<CallerIdentity> ResolveCallerAsync(
        int portalId,
        int? userId,
        CancellationToken cancellationToken)
    {
        if (userId is not int callerId)
        {
            return new CallerIdentity(
                Found: true,
                IsSuperUser: false,
                RoleNames: [_portalOptions.AllUsersRoleName, _portalOptions.UnauthenticatedRoleName]);
        }

        User? account = await _users.GetAsync(portalId, callerId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            account = await _users.GetAsync(portalId: null, callerId, cancellationToken).ConfigureAwait(false);
            if (account is null || !account.IsSuperUser)
            {
                return new CallerIdentity(Found: false, IsSuperUser: false, RoleNames: []);
            }
        }

        if (account.IsSuperUser)
        {
            return new CallerIdentity(Found: true, IsSuperUser: true, RoleNames: []);
        }

        IReadOnlyList<string> assigned = await _users
            .ListRoleNamesAsync(portalId, callerId, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        var roleNames = new List<string>(assigned.Count + 1);
        roleNames.AddRange(assigned);
        roleNames.Add(_portalOptions.AllUsersRoleName);

        return new CallerIdentity(Found: true, IsSuperUser: false, RoleNames: roleNames);
    }

    /// <summary>
    /// Tests whether an entity whose portal is optional belongs to the portal in question.
    /// </summary>
    /// <param name="owningPortalId">The portal recorded on the entity, absent for a host-level one.</param>
    /// <param name="portalId">The portal the question is asked within.</param>
    /// <returns><see langword="true"/> when the entity is in scope.</returns>
    /// <remarks>
    /// A host-level module or page carries no portal, and is in scope for every portal, which is what makes
    /// the host administration pages reachable from within a portal. Both zero and minus one are genuine
    /// identifiers in this schema, so the comparison is a real comparison rather than a sentinel test.
    /// </remarks>
    private static bool BelongsToPortal(int? owningPortalId, int portalId)
        => owningPortalId is null || owningPortalId.Value == portalId;

    /// <summary>
    /// Reduces a key sequence to the distinct, upper-cased keys in a stable order.
    /// </summary>
    /// <param name="keys">The keys to reduce.</param>
    /// <returns>The normalised keys.</returns>
    /// <remarks>
    /// The repository already upper-cases what it returns; normalising again here costs nothing and makes
    /// the guarantee hold for the catalogue projection too, whose column is stored as authored.
    /// </remarks>
    private static IReadOnlyList<string> Normalise(IEnumerable<string> keys)
        => keys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The outcome of resolving a caller: whether it exists, whether it is a host account, and the role
    /// names the evaluator must consider for it.
    /// </summary>
    /// <param name="Found">Whether the caller exists and is in scope.</param>
    /// <param name="IsSuperUser">Whether the caller is a host account and therefore holds everything.</param>
    /// <param name="RoleNames">The role names, including the applicable pseudo-roles.</param>
    private readonly record struct CallerIdentity(
        bool Found,
        bool IsSuperUser,
        IReadOnlyList<string> RoleNames);
}
